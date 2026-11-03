using System.Collections.Immutable;

namespace Zorg.Json.Emission;

/// <summary>The keyword spelling an emitted schema uses.</summary>
public enum JsonSchemaDialect
{
    /// <summary>JSON Schema Draft 2020-12.</summary>
    Draft202012,

    /// <summary>JSON Schema Draft-07: <c>definitions</c>, <c>dependencies</c> and array-form <c>items</c>.</summary>
    Draft07,

    /// <summary>The OpenAPI 3.0 schema object: <c>nullable</c>, boolean <c>exclusiveMinimum</c>, one <c>type</c>, no <c>$schema</c>.</summary>
    OpenApi30,

    /// <summary>The OpenAPI 3.1 schema object: Draft 2020-12 with no <c>$schema</c>.</summary>
    OpenApi31,
}

/// <summary>
/// What to emit: a dialect, plus steps that adapt the schema for a consumer such as an LLM provider.
/// Zod equivalent: the <c>target</c> option of <c>z.toJSONSchema</c>.
/// </summary>
/// <param name="Name">The profile's name.</param>
/// <param name="Dialect">The keyword spelling.</param>
/// <param name="Rewriters">Model changes, applied in order before emission. Empty for the built-in profiles.</param>
/// <param name="PostProcessors">JSON changes, applied in order after emission. Empty for the built-in profiles.</param>
/// <example>
/// <code>
/// JsonObject schema = Z.String().ToJsonSchema(new() { Target = JsonSchemaProfile.Draft07 });
/// </code>
/// </example>
public sealed record JsonSchemaProfile(
    string Name,
    JsonSchemaDialect Dialect,
    ImmutableArray<ISchemaRewriter> Rewriters,
    ImmutableArray<IJsonSchemaPostProcessor> PostProcessors)
{
    /// <summary>JSON Schema Draft 2020-12, the default.</summary>
    public static JsonSchemaProfile Draft202012 { get; } = new("draft-2020-12", JsonSchemaDialect.Draft202012, [], []);

    /// <summary>JSON Schema Draft-07, for SchemaStore, VS Code and AsyncAPI.</summary>
    public static JsonSchemaProfile Draft07 { get; } = new("draft-07", JsonSchemaDialect.Draft07, [], []);

    /// <summary>The OpenAPI 3.0 schema object, also used by Kubernetes CRDs.</summary>
    public static JsonSchemaProfile OpenApi30 { get; } = new("openapi-3.0", JsonSchemaDialect.OpenApi30, [], []);

    /// <summary>The OpenAPI 3.1 schema object.</summary>
    public static JsonSchemaProfile OpenApi31 { get; } = new("openapi-3.1", JsonSchemaDialect.OpenApi31, [], []);

    /// <summary>Adds rewriters to a copy of this profile.</summary>
    /// <param name="rewriters">The rewriters, which run after the existing ones.</param>
    /// <returns>The new profile.</returns>
    public JsonSchemaProfile With(params ISchemaRewriter[] rewriters) => this with { Rewriters = Rewriters.AddRange(rewriters) };

    /// <summary>Adds post-processors to a copy of this profile.</summary>
    /// <param name="postProcessors">The post-processors, which run after the existing ones.</param>
    /// <returns>The new profile.</returns>
    public JsonSchemaProfile With(params IJsonSchemaPostProcessor[] postProcessors) => this with { PostProcessors = PostProcessors.AddRange(postProcessors) };

    internal bool WritesSchemaKeyword => Dialect is JsonSchemaDialect.Draft202012 or JsonSchemaDialect.Draft07 && Name.Length > 0;

    internal string MetaSchema => Dialect == JsonSchemaDialect.Draft07
        ? "http://json-schema.org/draft-07/schema#"
        : "https://json-schema.org/draft/2020-12/schema";

    internal string DefinitionsKeyword => Dialect == JsonSchemaDialect.Draft07 ? "definitions" : "$defs";
}
