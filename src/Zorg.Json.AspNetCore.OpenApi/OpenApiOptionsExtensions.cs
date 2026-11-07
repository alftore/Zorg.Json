using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using Zorg.Json.Emission;
using Zorg.Json.Model;

namespace Zorg.Json.AspNetCore.OpenApi;

/// <summary>Adds Zorg.Json schemas to OpenAPI documents.</summary>
public static class OpenApiOptionsExtensions
{
    /// <summary>
    /// Replaces the generated schema of each body marked with <c>ValidateBody</c> or <c>ProducesSchema</c>. Request bodies
    /// are emitted with <see cref="SchemaDirection.Input"/> and responses with <see cref="SchemaDirection.Output"/>. Schemas
    /// marked with <c>Named</c> become components, with an <c>Output.</c> prefix for responses because the two can differ.
    /// </summary>
    /// <param name="options">The OpenAPI options.</param>
    /// <returns>The options, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Reference <c>Microsoft.AspNetCore.OpenApi</c> directly from the app as well: its MSBuild targets don't flow through
    /// this package, and its source generator fails with CS9137 without them when the app generates a documentation file.
    /// </para>
    /// <para>
    /// Under Native AOT, also call <c>AddProblemDetails</c>. <c>ValidateBody</c> documents a 400 response, and the document
    /// generator needs JSON metadata for that type from the app's options.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddOpenApi(options => options.AddEndpointSchemas());
    /// </code>
    /// </example>
    public static OpenApiOptions AddEndpointSchemas(this OpenApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.AddOperationTransformer(new EndpointSchemaTransformer());
    }

    // An operation transformer, not a schema transformer: one CLR type is one shared component in the
    // document, so a schema transformer can't give the same type different request and response schemas.
    private sealed class EndpointSchemaTransformer : IOpenApiOperationTransformer
    {
        private const string JsonContentType = "application/json";
        private const string ComponentPrefix = "#/components/schemas/";
        private const string OutputPrefix = "Output.";

        // The text of each component by id, per document, to tell a shared name from a clash.
        private static readonly ConditionalWeakTable<OpenApiDocument, Dictionary<string, string>> Components = new();

        public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
        {
            OpenApiDocument document = context.Document ?? throw new InvalidOperationException("The operation has no document.");

            OpenApiSpecVersion version = context.ApplicationServices.GetService<IOptionsMonitor<OpenApiOptions>>()?.Get(context.DocumentName).OpenApiVersion
                ?? OpenApiSpecVersion.OpenApi3_1;
            JsonSchemaProfile profile = version == OpenApiSpecVersion.OpenApi3_0 ? DowngradedProfile : Profile;

            foreach (EndpointSchemaMetadata metadata in context.Description.ActionDescriptor.EndpointMetadata.OfType<EndpointSchemaMetadata>())
            {
                OpenApiSchema schema = Convert(metadata, document, profile);

                if (metadata.Direction == SchemaDirection.Input)
                {
                    SetRequestBody(operation, schema);
                }
                else
                {
                    SetResponse(operation, metadata.StatusCode.GetValueOrDefault(200), schema);
                }
            }

            return Task.CompletedTask;
        }

        private static void SetRequestBody(OpenApiOperation operation, OpenApiSchema schema)
        {
            OpenApiRequestBody body = operation.RequestBody as OpenApiRequestBody ?? new OpenApiRequestBody { Required = true };
            body.Content ??= new Dictionary<string, OpenApiMediaType>();
            body.Content[JsonContentType] = new OpenApiMediaType { Schema = schema };
            operation.RequestBody = body;
        }

        private static void SetResponse(OpenApiOperation operation, int statusCode, OpenApiSchema schema)
        {
            string key = statusCode.ToString(CultureInfo.InvariantCulture);
            operation.Responses ??= [];

            if (!operation.Responses.TryGetValue(key, out IOpenApiResponse? found) || found is not OpenApiResponse response)
            {
                response = new OpenApiResponse { Description = "Success" };
                operation.Responses[key] = response;
            }

            response.Content ??= new Dictionary<string, OpenApiMediaType>();
            response.Content[JsonContentType] = new OpenApiMediaType { Schema = schema };
        }

        // What Microsoft.OpenApi's model would make stricter:
        // - It has no prefixItems, and keeps a closing items: false without them, which refuses every tuple.
        // - It holds const as text, so 3 and true become "3" and "True". A one-value enum stays JSON.
        // - It reads a boolean schema in properties as null.
        private static readonly JsonSchemaProfile Profile =
            JsonSchemaProfile.OpenApi31.With(Rewriters.RelocateToDescription("prefixItems"), new ConstAsEnum(), Rewriters.ConvertBooleanSchemas);

        // Writing 3.0, it moves the keywords 3.0 lacks to extensions or drops them. That's looser where they apply, and stricter
        // under not or in a oneOf branch, so they're moved here, where the emitter knows which applicators that touches.
        // patternProperties also leaves what closes the object behind, which then refuses those names.
        private static readonly JsonSchemaProfile DowngradedProfile = JsonSchemaProfile.OpenApi31.With(
            Rewriters.RelocateToDescription(
                "prefixItems", "patternProperties", "contains", "minContains", "maxContains", "propertyNames", "dependentRequired",
                "dependentSchemas", "unevaluatedProperties", "unevaluatedItems", "if", "then", "else"),
            new ConstAsEnum(),
            Rewriters.ConvertBooleanSchemas);

        private sealed class ConstAsEnum : ISchemaRewriter
        {
            public SchemaNode Rewrite(SchemaNode node, RewriteContext context)
            {
                // Microsoft.OpenApi reads a number past the range of double as Infinity, which isn't JSON. Without the
                // value the schema is only looser, and default and examples only annotate.
                if (node.Const is { } huge && IsPastDouble(huge))
                {
                    node = node with { Const = null };
                }

                if (node.Enum.Any(IsPastDouble))
                {
                    node = node with { Enum = [] };
                }

                if (node.Default is { } fallback && IsPastDouble(fallback))
                {
                    node = node with { Default = null };
                }

                if (node.Examples.Any(IsPastDouble))
                {
                    node = node with { Examples = [] };
                }

                // It holds multipleOf as a decimal, which throws past its range and rounds past 28 digits. Without it the
                // schema is only looser.
                if (node.MultipleOf is { } divisor && !FitsDecimal(divisor))
                {
                    node = node with { MultipleOf = null };
                }

                // Beside an enum, the enum alone is only looser.
                return node.Const is { } constant
                    ? node with { Const = null, Enum = node.Enum.IsEmpty ? [constant] : node.Enum }
                    : node;
            }

            private static bool FitsDecimal(System.Text.Json.JsonElement value)
            {
                string text = value.GetRawText();
                int exponentAt = text.IndexOfAny(['e', 'E']);
                string mantissa = (exponentAt < 0 ? text : text[..exponentAt]).TrimStart('-').Replace(".", string.Empty, StringComparison.Ordinal).Trim('0');

                return mantissa.Length <= 28
                    && value.TryGetDecimal(out decimal number)
                    && number != 0;
            }

            private static bool IsPastDouble(System.Text.Json.JsonElement value) => value.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Number => !value.TryGetDouble(out double number) || double.IsInfinity(number),
                System.Text.Json.JsonValueKind.Array => value.EnumerateArray().Any(IsPastDouble),
                System.Text.Json.JsonValueKind.Object => value.EnumerateObject().Any(property => IsPastDouble(property.Value)),
                _ => false,
            };
        }

        private static OpenApiSchema Convert(EndpointSchemaMetadata metadata, OpenApiDocument document, JsonSchemaProfile profile)
        {
            string namePrefix = metadata.Direction == SchemaDirection.Output ? OutputPrefix : string.Empty;

            EmitResult emitted = metadata.Emit(new JsonSchemaOptions
            {
                Target = profile,
                Direction = metadata.Direction,
                Reused = ReusedSchemaHandling.Ref,
                ReferencePrefix = ComponentPrefix + namePrefix,
            });

            foreach ((string name, JsonObject definition) in emitted.Definitions)
            {
                string id = namePrefix + name;
                string text = definition.ToJsonString();
                Dictionary<string, string> known = Components.GetOrCreateValue(document);

                if (known.TryGetValue(id, out string? existing))
                {
                    if (existing != text)
                    {
                        throw new InvalidOperationException(
                            $"Two different schemas are named '{name}'. A change after Named(), such as Nullable() or Default(), makes another schema with the same name. Give one a different Named() so the OpenAPI components don't collide.");
                    }
                }
                else if (document.Components?.Schemas?.ContainsKey(id) == true)
                {
                    // The framework's schema of a type by that name. Sharing it would put its rules where Zorg's apply.
                    throw new InvalidOperationException(
                        $"The document already has a component named '{id}', for a .NET type. Give the schema a different Named() so the two don't share it.");
                }
                else
                {
                    known[id] = text;
                    document.AddComponent<IOpenApiSchema>(id, Parse(definition, document));
                }
            }

            return Parse(emitted.Schema, document);
        }

        // Always read as 3.1: Microsoft.OpenApi writes the schema object in the document's own version.
        private static OpenApiSchema Parse(JsonObject schema, OpenApiDocument document) =>
            OpenApiModelFactory.Parse<OpenApiSchema>(WithoutTopReference(schema).ToJsonString(), OpenApiSpecVersion.OpenApi3_1, document, out _, "json")
                ?? throw new InvalidOperationException("Microsoft.OpenApi couldn't read an emitted schema.");

        // A $ref at the top reads as a reference object, not a schema, so it goes in an allOf, which means the same.
        // It goes last, because a pointer can name an entry of that allOf by its index.
        private static JsonObject WithoutTopReference(JsonObject schema)
        {
            if (!schema.ContainsKey("$ref"))
            {
                return schema;
            }

            JsonObject copy = (JsonObject)schema.DeepClone();
            JsonObject reference = new() { ["$ref"] = copy["$ref"]!.DeepClone() };
            copy.Remove("$ref");

            if (copy["allOf"] is JsonArray allOf)
            {
                allOf.Add((JsonNode)reference);
            }
            else
            {
                copy["allOf"] = new JsonArray(reference);
            }

            return copy;
        }
    }
}
