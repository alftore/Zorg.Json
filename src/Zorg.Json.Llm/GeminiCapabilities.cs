using System;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Zorg.Json.Llm;

/// <summary>Dated tables of what Gemini structured output accepts. The tables describe keywords, not the request field that carries them.</summary>
/// <example>
/// <code>
/// bool recursive = GeminiCapabilities.JsonSchemaAsOf2026_11.SupportsRecursion;
/// </code>
/// </example>
public static class GeminiCapabilities
{
    /// <summary>
    /// The JSON Schema subset on 2026-11-03, for <c>response_format.schema</c> in the Interactions API and
    /// <c>generationConfig.responseJsonSchema</c> in <c>generateContent</c>. The page documents the first; that the
    /// second takes the same subset is assumed. <c>minLength</c> and similar are stripped by the API without an error.
    /// </summary>
    [SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "The date stays readable, and old tables stay beside new ones.")]
    public static SchemaCapabilities JsonSchemaAsOf2026_11 { get; } = new()
    {
        Provider = "Gemini",
        AsOf = new DateOnly(2026, 11, 3),
        Source = new Uri("https://aistudio.google.com/docs/structured-output"),
        Keywords = ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "type", "title", "description", "enum", "anyOf", "$ref", "$defs",
            "properties", "required", "additionalProperties", "items", "prefixItems", "minItems", "maxItems",
            "format", "minimum", "maximum"),
        Formats = ImmutableHashSet.Create(StringComparer.Ordinal, "date-time", "date", "time"),
        SupportsRecursion = true,
    };

    /// <summary>
    /// The <c>Schema</c> object on 2026-11-03, "a select subset of an OpenAPI 3.0 schema object". It has no <c>$ref</c>
    /// or <c>additionalProperties</c>, and any <c>format</c> is allowed.
    /// </summary>
    [SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "The date stays readable, and old tables stay beside new ones.")]
    public static SchemaCapabilities OpenApiAsOf2026_11 { get; } = new()
    {
        Provider = "Gemini",
        AsOf = new DateOnly(2026, 11, 3),
        Source = new Uri("https://aistudio.google.com/api-reference/caching"),
        Keywords = ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "type", "format", "title", "description", "nullable", "enum", "items", "minItems", "maxItems",
            "properties", "required", "minProperties", "maxProperties", "minimum", "maximum",
            "minLength", "maxLength", "pattern", "example", "anyOf", "propertyOrdering", "default"),
    };
}
