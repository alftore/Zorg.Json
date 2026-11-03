using System;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Zorg.Json.Llm;

/// <summary>Dated tables of what Anthropic structured outputs and strict tool use accept.</summary>
/// <example>
/// <code>
/// int? optional = AnthropicCapabilities.AsOf2026_11.MaxOptionalProperties;
/// </code>
/// </example>
public static class AnthropicCapabilities
{
    /// <summary>
    /// The state on 2026-11-03. <c>oneOf</c> and <c>prefixItems</c> are left out because the docs don't list them.
    /// <c>MaxQuantifierBound</c> is our own threshold: the docs only say "large ranges" may fail. An object root is
    /// assumed, since tools need one and the docs only show objects.
    /// </summary>
    [SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "The date stays readable, and old tables stay beside new ones.")]
    public static SchemaCapabilities AsOf2026_11 { get; } = new()
    {
        Provider = "Anthropic",
        AsOf = new DateOnly(2026, 11, 3),
        Source = new Uri("https://platform.claude.com/docs/en/build-with-claude/structured-outputs"),
        Keywords = ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "type", "title", "description", "enum", "const", "anyOf", "allOf", "$ref", "$defs", "definitions", "default",
            "properties", "required", "additionalProperties", "items", "minItems", "pattern", "format"),
        Formats = ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "date-time", "time", "date", "duration", "email", "hostname", "uri", "ipv4", "ipv6", "uuid"),
        MaxMinItems = 1,
        ScalarEnumsOnly = true,
        UnsupportedRegex = RegexFeatures.Backreference | RegexFeatures.Lookaround | RegexFeatures.WordBoundary,
        MaxQuantifierBound = 100,
        RequiresObjectRoot = true,
        RequiresClosedObjects = true,
        RequiresExplicitType = true,
        MaxOptionalProperties = 24,
        MaxUnionProperties = 16,
        MaxStrictTools = 20,
    };
}
