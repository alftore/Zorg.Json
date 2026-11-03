using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using Zorg.Json.Emission;

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

        public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
        {
            OpenApiDocument document = context.Document ?? throw new InvalidOperationException("The operation has no document.");

            foreach (EndpointSchemaMetadata metadata in context.Description.ActionDescriptor.EndpointMetadata.OfType<EndpointSchemaMetadata>())
            {
                OpenApiSchema schema = Convert(metadata, document);

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

        private static OpenApiSchema Convert(EndpointSchemaMetadata metadata, OpenApiDocument document)
        {
            string namePrefix = metadata.Direction == SchemaDirection.Output ? OutputPrefix : string.Empty;

            EmitResult emitted = metadata.Emit(new JsonSchemaOptions
            {
                Target = JsonSchemaProfile.OpenApi31,
                Direction = metadata.Direction,
                Reused = ReusedSchemaHandling.Ref,
                ReferencePrefix = ComponentPrefix + namePrefix,
            });

            foreach ((string name, JsonObject definition) in emitted.Definitions)
            {
                string id = namePrefix + name;

                if (document.Components?.Schemas?.ContainsKey(id) != true)
                {
                    document.AddComponent<IOpenApiSchema>(id, Parse(definition, document));
                }
            }

            return Parse(emitted.Schema, document);
        }

        // Always read as 3.1: Microsoft.OpenApi writes the schema object in the document's own version.
        private static OpenApiSchema Parse(JsonObject schema, OpenApiDocument document) =>
            OpenApiModelFactory.Parse<OpenApiSchema>(schema.ToJsonString(), OpenApiSpecVersion.OpenApi3_1, document, out _, "json")
                ?? throw new InvalidOperationException("Microsoft.OpenApi couldn't read an emitted schema.");
    }
}
