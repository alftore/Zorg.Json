using System;
using System.Collections.Immutable;
using System.Linq;

namespace Zorg.Json.Llm;

/// <summary>
/// What a provider accepted on a given date, as data. Providers change these without versioning them, so each table is
/// dated and old ones stay selectable. Pass one to <see cref="LlmSchemaExtensions.CheckCompatibility"/> or to a
/// <c>Create</c> method of <see cref="LlmProfiles"/>.
/// </summary>
/// <example>
/// <code>
/// SchemaCapabilities tighter = OpenAICapabilities.AsOf2026_11 with { MaxNestingDepth = 5 };
/// JsonSchemaProfile profile = LlmProfiles.CreateOpenAIStrict(tighter);
/// </code>
/// </example>
public sealed record SchemaCapabilities
{
    private static readonly ImmutableArray<string> KnownKeywords =
    [
        "title", "deprecated", "readOnly", "writeOnly", "examples", "const", "enum",
        "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf",
        "minLength", "maxLength", "pattern", "format", "contentEncoding", "contentMediaType", "contentSchema",
        "prefixItems", "contains", "minContains", "maxContains", "minItems", "maxItems", "uniqueItems",
        "patternProperties", "propertyNames", "dependentRequired", "dependentSchemas", "minProperties", "maxProperties",
        "allOf", "anyOf", "oneOf", "not", "if", "then", "else", "unevaluatedItems", "unevaluatedProperties",
    ];

    /// <summary>The provider's name.</summary>
    public required string Provider { get; init; }

    /// <summary>The date the table was checked against the source.</summary>
    public required DateOnly AsOf { get; init; }

    /// <summary>The documentation the table was taken from.</summary>
    public required Uri Source { get; init; }

    /// <summary>The JSON Schema keywords the provider accepts. Anything else moves to <c>description</c>.</summary>
    public required ImmutableHashSet<string> Keywords { get; init; }

    /// <summary>The accepted values of <c>format</c>. Null means any value.</summary>
    public ImmutableHashSet<string>? Formats { get; init; }

    /// <summary>The largest accepted <c>minItems</c>. Null means no limit.</summary>
    public int? MaxMinItems { get; init; }

    /// <summary>Whether <c>enum</c> can only hold strings, numbers, booleans and <c>null</c>.</summary>
    public bool ScalarEnumsOnly { get; init; }

    /// <summary>Regular expression features that make the provider reject a <c>pattern</c>.</summary>
    public RegexFeatures UnsupportedRegex { get; init; }

    /// <summary>The largest <c>{n,m}</c> bound that's safe in a <c>pattern</c>. Null means no known limit.</summary>
    public int? MaxQuantifierBound { get; init; }

    /// <summary>Whether the root must be <c>type: object</c>.</summary>
    public bool RequiresObjectRoot { get; init; }

    /// <summary>Whether every object needs <c>additionalProperties: false</c>.</summary>
    public bool RequiresClosedObjects { get; init; }

    /// <summary>Whether every property must be listed in <c>required</c>.</summary>
    public bool RequiresAllProperties { get; init; }

    /// <summary>Whether every schema must say what it is, with <c>type</c>, <c>$ref</c>, <c>anyOf</c>, <c>enum</c> or <c>const</c>.</summary>
    public bool RequiresExplicitType { get; init; }

    /// <summary>Whether a <c>$ref</c> may lead back to itself.</summary>
    public bool SupportsRecursion { get; init; }

    /// <summary>The most object properties in one schema, counted over all objects.</summary>
    public int? MaxProperties { get; init; }

    /// <summary>The most levels of nested objects.</summary>
    public int? MaxNestingDepth { get; init; }

    /// <summary>The most <c>enum</c> values, counted over all enums.</summary>
    public int? MaxEnumValues { get; init; }

    /// <summary>The most characters in the strings of one <c>enum</c> that has more than <see cref="LongEnumThreshold"/> values.</summary>
    public int? MaxLongEnumLength { get; init; }

    /// <summary>The number of values above which <see cref="MaxLongEnumLength"/> applies.</summary>
    public int? LongEnumThreshold { get; init; }

    /// <summary>The most characters over all property names, definition names, enum values and const values.</summary>
    public int? MaxTotalStringLength { get; init; }

    /// <summary>The most properties left out of <c>required</c>. A per-request total, so one schema can only use part of it.</summary>
    public int? MaxOptionalProperties { get; init; }

    /// <summary>The most properties using <c>anyOf</c> or a type array. A per-request total, so one schema can only use part of it.</summary>
    public int? MaxUnionProperties { get; init; }

    /// <summary>The most strict tools in one request. A schema can't exceed it alone, so it's recorded here and not checked.</summary>
    public int? MaxStrictTools { get; init; }

    /// <summary>The known keywords the provider doesn't accept, for <see cref="Emission.Rewriters.RelocateToDescription"/>.</summary>
    public ImmutableArray<string> UnsupportedKeywords => [.. KnownKeywords.Where(keyword => !Keywords.Contains(keyword))];
}

/// <summary>Regular expression features a provider may refuse.</summary>
[Flags]
public enum RegexFeatures
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>Backreferences such as <c>\1</c> and <c>\k&lt;name&gt;</c>.</summary>
    Backreference = 1,

    /// <summary>Lookahead and lookbehind.</summary>
    Lookaround = 2,

    /// <summary>The word boundaries <c>\b</c> and <c>\B</c>.</summary>
    WordBoundary = 4,
}
