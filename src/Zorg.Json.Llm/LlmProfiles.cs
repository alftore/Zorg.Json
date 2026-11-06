using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zorg.Json.Emission;
using Zorg.Json.Model;

namespace Zorg.Json.Llm;

/// <summary>
/// Emission profiles for provider structured outputs. Each one is a composition of <see cref="Rewriters"/> and a dated
/// <see cref="SchemaCapabilities"/> table, which also moves what no rewriter caught to <c>description</c>. An emitted
/// schema steers the model. Validate the answer with
/// <see cref="LlmSchemaExtensions.ParseModelOutput{T}(Schema{T}, EmitResult, string)"/>, which enforces the full schema.
/// </summary>
/// <example>
/// <code>
/// EmitResult emitted = schema.Emit(LlmProfiles.OpenAIStrict);
/// string json = emitted.Schema.ToJsonString();
/// </code>
/// </example>
public static class LlmProfiles
{
    private static readonly ISchemaRewriter OneOfToAnyOf = new OneOfRewriter();

    private static readonly ISchemaRewriter ConstToEnum = new ConstRewriter();

    /// <summary>OpenAI Structured Outputs and strict function calling, as of the latest table.</summary>
    public static JsonSchemaProfile OpenAIStrict { get; } = CreateOpenAIStrict(OpenAICapabilities.AsOf2026_11);

    /// <summary>Anthropic structured outputs and strict tool use, as of the latest table. Optional properties stay optional.</summary>
    public static JsonSchemaProfile AnthropicStrict { get; } = CreateAnthropicStrict(AnthropicCapabilities.AsOf2026_11);

    /// <summary>Gemini's JSON Schema subset, as of the latest table. Send it as <c>response_format.schema</c> or <c>responseJsonSchema</c>.</summary>
    public static JsonSchemaProfile GeminiJsonSchema { get; } = CreateGeminiJsonSchema(GeminiCapabilities.JsonSchemaAsOf2026_11);

    /// <summary>Gemini's older OpenAPI 3.0 <c>Schema</c> object, as of the latest table: <c>nullable</c>, upper-case types and no <c>$ref</c>.</summary>
    public static JsonSchemaProfile GeminiOpenApi { get; } = CreateGeminiOpenApi(GeminiCapabilities.OpenApiAsOf2026_11);

    /// <summary>Creates the OpenAI profile for a chosen table.</summary>
    /// <param name="capabilities">The table.</param>
    /// <returns>The profile.</returns>
    /// <example>
    /// <code>
    /// JsonSchemaProfile profile = LlmProfiles.CreateOpenAIStrict(OpenAICapabilities.AsOf2026_11);
    /// </code>
    /// </example>
    public static JsonSchemaProfile CreateOpenAIStrict(SchemaCapabilities capabilities) => Create(
        "openai-strict",
        JsonSchemaDialect.OpenApi31,
        capabilities,
        Rewriters.RequireObjectRoot,
        Rewriters.CloseAllObjects,
        Rewriters.RequireAllAsNullable,
        OneOfToAnyOf,
        Rewriters.MoveDefaultToDescription);

    /// <summary>Creates the Anthropic profile for a chosen table.</summary>
    /// <param name="capabilities">The table.</param>
    /// <returns>The profile.</returns>
    /// <example>
    /// <code>
    /// JsonSchemaProfile profile = LlmProfiles.CreateAnthropicStrict(AnthropicCapabilities.AsOf2026_11);
    /// </code>
    /// </example>
    public static JsonSchemaProfile CreateAnthropicStrict(SchemaCapabilities capabilities) => Create(
        "anthropic-strict",
        JsonSchemaDialect.OpenApi31,
        capabilities,
        Rewriters.InlineOrRejectRecursion,
        Rewriters.RequireObjectRoot,
        Rewriters.CloseAllObjects,
        OneOfToAnyOf);

    /// <summary>Creates the Gemini JSON Schema profile for a chosen table.</summary>
    /// <param name="capabilities">The table.</param>
    /// <returns>The profile.</returns>
    /// <example>
    /// <code>
    /// JsonSchemaProfile profile = LlmProfiles.CreateGeminiJsonSchema(GeminiCapabilities.JsonSchemaAsOf2026_11);
    /// </code>
    /// </example>
    public static JsonSchemaProfile CreateGeminiJsonSchema(SchemaCapabilities capabilities) => Create(
        "gemini-json-schema",
        JsonSchemaDialect.OpenApi31,
        capabilities,
        Rewriters.CloseAllObjects,
        OneOfToAnyOf,
        ConstToEnum,
        Rewriters.MoveDefaultToDescription);

    /// <summary>Creates the Gemini OpenAPI profile for a chosen table.</summary>
    /// <param name="capabilities">The table.</param>
    /// <returns>The profile.</returns>
    /// <example>
    /// <code>
    /// JsonSchemaProfile profile = LlmProfiles.CreateGeminiOpenApi(GeminiCapabilities.OpenApiAsOf2026_11);
    /// </code>
    /// </example>
    public static JsonSchemaProfile CreateGeminiOpenApi(SchemaCapabilities capabilities) =>
        Create("gemini-openapi", JsonSchemaDialect.OpenApi30, capabilities, Rewriters.InlineOrRejectRecursion, OneOfToAnyOf, ConstToEnum)
            .With(new UpperCaseTypes());

    // OpenApi31 is Draft 2020-12 without $schema, which no provider asks for.
    private static JsonSchemaProfile Create(string name, JsonSchemaDialect dialect, SchemaCapabilities capabilities, params ReadOnlySpan<ISchemaRewriter> rewriters)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        return new JsonSchemaProfile(name, dialect, [], [])
            .With([.. rewriters, Rewriters.RelocateToDescription(capabilities.UnsupportedKeywords.AsSpan()), Rewriters.ConvertBooleanSchemas])
            .With(new CapabilityEnforcer(capabilities));
    }

    private sealed class OneOfRewriter : ISchemaRewriter
    {
        public SchemaNode Rewrite(SchemaNode node, RewriteContext context)
        {
            if (node.OneOf.IsEmpty || !node.AnyOf.IsEmpty)
            {
                return node;
            }

            context.Report(new EmitDiagnostic("oneOf", $"{context.Options.Target.Name} has no oneOf, so it became anyOf, which also accepts several matches."));
            return node with { OneOf = [], AnyOf = node.OneOf };
        }
    }

    private sealed class ConstRewriter : ISchemaRewriter
    {
        public SchemaNode Rewrite(SchemaNode node, RewriteContext context) =>
            node.Const is { ValueKind: JsonValueKind.String or JsonValueKind.Number } constant
                ? node with { Const = null, Enum = [constant] }
                : node;
    }

    private sealed class UpperCaseTypes : IJsonSchemaPostProcessor
    {
        public void Process(JsonObject schema, EmitContext context) => JsonSchemaWalker.Walk(schema, (node, _, _) =>
        {
            if (node["type"] is JsonValue type && type.TryGetValue(out string? name))
            {
                node["type"] = name.ToUpperInvariant();
            }
        });
    }
}
