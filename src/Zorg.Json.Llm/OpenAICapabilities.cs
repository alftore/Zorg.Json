using System;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Zorg.Json.Llm;

/// <summary>Dated tables of what OpenAI Structured Outputs and strict function calling accept.</summary>
/// <example>
/// <code>
/// int? depth = OpenAICapabilities.AsOf2026_11.MaxNestingDepth;
/// </code>
/// </example>
public static class OpenAICapabilities
{
    /// <summary>
    /// The state on 2026-11-03. <c>minLength</c>, <c>maxLength</c> and <c>patternProperties</c> are left out: the guide
    /// doesn't list them as supported, though it only names them as unavailable on fine-tuned models. The guide doesn't
    /// list <c>title</c> or <c>description</c> either, and they're assumed to be accepted.
    /// </summary>
    [SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "The date stays readable, and old tables stay beside new ones.")]
    public static SchemaCapabilities AsOf2026_11 { get; } = new()
    {
        Provider = "OpenAI",
        AsOf = new DateOnly(2026, 11, 3),
        Source = new Uri("https://developers.openai.com/api/docs/guides/structured-outputs#supported-schemas"),
        Keywords = ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "type", "title", "description", "enum", "const", "anyOf", "$ref", "$defs",
            "properties", "required", "additionalProperties", "items", "minItems", "maxItems",
            "pattern", "format", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf"),
        Formats = ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "date-time", "time", "date", "duration", "email", "hostname", "ipv4", "ipv6", "uuid"),
        RequiresObjectRoot = true,
        RequiresClosedObjects = true,
        RequiresAllProperties = true,
        RequiresExplicitType = true,
        SupportsRecursion = true,
        MaxProperties = 5000,
        MaxNestingDepth = 10,
        MaxEnumValues = 1000,
        MaxLongEnumLength = 15_000,
        LongEnumThreshold = 250,
        MaxTotalStringLength = 120_000,
    };
}
