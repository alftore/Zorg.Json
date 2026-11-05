using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zorg.Json.Emission;

namespace Zorg.Json.Llm;

/// <summary>
/// Reads the full schema for what the provider won't enforce, with exact locations, then the emitted schema for what
/// the provider would refuse.
/// </summary>
internal sealed class CompatibilityChecker(SchemaCapabilities capabilities, string profileName)
{
    private static readonly ImmutableHashSet<string> Annotations =
        ImmutableHashSet.Create(StringComparer.Ordinal, "default", "examples", "deprecated", "readOnly", "writeOnly", "title", "contentEncoding", "contentMediaType");

    private static readonly string[] TypeKeywords = ["type", "$ref", "anyOf", "allOf", "enum", "const"];

    private readonly List<CompatibilityIssue> _issues = [];

    public CompatibilityReport Check(EmitResult full, EmitResult emitted, bool wrappedRoot)
    {
        JsonSchemaWalker.Walk(full.Schema, CheckFullSchema);

        if (wrappedRoot)
        {
            Add(CompatibilitySeverity.Info, CompatibilityIssueKind.Root, null, $"The root is wrapped in an object with a \"{Rewriters.RootWrapperProperty}\" property. ParseModelOutput unwraps it.");
        }

        foreach (EmitDiagnostic diagnostic in full.Diagnostics)
        {
            bool isRefinement = diagnostic.Keyword is null;

            Add(
                CompatibilitySeverity.Warning,
                isRefinement ? CompatibilityIssueKind.Refinement : CompatibilityIssueKind.Relocated,
                diagnostic.Keyword,
                diagnostic.Message,
                diagnostic.Location);
        }

        foreach (EmitDiagnostic diagnostic in emitted.Diagnostics.Where(diagnostic => diagnostic.Keyword == "$ref" && !LocalReferences.IsDangling(diagnostic)))
        {
            Add(CompatibilitySeverity.Error, CompatibilityIssueKind.Recursion, "$ref", diagnostic.Message);
        }

        foreach ((string location, string reference) in LocalReferences.Dangling(emitted.Schema))
        {
            Add(CompatibilitySeverity.Error, CompatibilityIssueKind.Reference, "$ref", LocalReferences.DanglingMessage(reference), location);
        }

        CheckRoot(emitted.Schema);
        Counters counters = new();
        JsonSchemaWalker.Walk(emitted.Schema, (node, location, depth) => CheckEmitted(node, location, depth, counters));
        CheckLimits(counters);

        return new CompatibilityReport(profileName, capabilities, [.. _issues.OrderBy(issue => issue.Severity)]);
    }

    private static bool IsType(JsonObject node, string type) => node["type"] switch
    {
        JsonValue value => string.Equals(LocalReferences.AsString(value), type, StringComparison.OrdinalIgnoreCase),
        JsonArray values => values.Any(value => string.Equals(LocalReferences.AsString(value), type, StringComparison.OrdinalIgnoreCase)),
        _ => false,
    };

    private static bool IsUnion(JsonNode? property) => property is JsonObject schema && (schema["type"] is JsonArray || schema.ContainsKey("anyOf"));

    private static bool IsExactRewrite(string keyword, JsonNode? value, SchemaCapabilities capabilities) =>
        keyword == "const" && capabilities.Keywords.Contains("enum") && value?.GetValueKind() is JsonValueKind.String or JsonValueKind.Number;

    private void Add(CompatibilitySeverity severity, CompatibilityIssueKind kind, string? keyword, string message, string location = "") =>
        _issues.Add(new CompatibilityIssue(severity, kind, keyword, message, location));

    private void CheckFullSchema(JsonObject node, string location, int depth)
    {
        foreach ((string keyword, JsonNode? value) in node.ToArray())
        {
            if (keyword == "$schema"
                || IsExactRewrite(keyword, value, capabilities)
                || CapabilityRules.Check(capabilities, keyword, value) is not KeywordVerdict.Relocate)
            {
                continue;
            }

            CompatibilitySeverity severity = Annotations.Contains(keyword) ? CompatibilitySeverity.Info : CompatibilitySeverity.Warning;
            Add(severity, CompatibilityIssueKind.Relocated, keyword, $"{capabilities.Provider} won't enforce this {keyword}. It moves to description or a looser form, and ParseModelOutput still enforces it.", location);
        }

        if (capabilities.RequiresClosedObjects && node["additionalProperties"] is JsonObject)
        {
            Add(CompatibilitySeverity.Error, CompatibilityIssueKind.OpenObject, "additionalProperties", $"{capabilities.Provider} needs closed objects, so the model can only answer with the declared properties.", location);
        }

        if (capabilities.RequiresAllProperties && node["properties"] is JsonObject properties)
        {
            HashSet<string?> required = node["required"] is JsonArray names ? [.. names.Select(LocalReferences.AsString)] : [];

            foreach (string name in properties.Select(property => property.Key).Where(name => !required.Contains(name)))
            {
                Add(CompatibilitySeverity.Info, CompatibilityIssueKind.OptionalProperty, "required", $"The optional property \"{name}\" becomes required and nullable. ParseModelOutput turns null back into absent.", location);
            }
        }
    }

    private void CheckRoot(JsonObject root)
    {
        if (capabilities.RequiresObjectRoot && (!IsType(root, "object") || root["type"] is JsonArray || root.ContainsKey("anyOf")))
        {
            Add(CompatibilitySeverity.Error, CompatibilityIssueKind.Root, null, $"{capabilities.Provider} needs a root of type object, without anyOf.");
        }
    }

    private void CheckEmitted(JsonObject node, string location, int depth, Counters counters)
    {
        foreach ((string keyword, JsonNode? value) in node.ToArray())
        {
            if (CapabilityRules.Check(capabilities, keyword, value) != KeywordVerdict.Allowed)
            {
                Add(CompatibilitySeverity.Error, CompatibilityIssueKind.UnsupportedKeyword, keyword, $"{capabilities.Provider} doesn't accept this {keyword}.", location);
            }
        }

        CheckShape(node, location);
        CheckPattern(node, location);
        counters.Depth = Math.Max(counters.Depth, depth);
        Count(node, location, counters);
    }

    private void CheckShape(JsonObject node, string location)
    {
        if (capabilities.RequiresExplicitType && !TypeKeywords.Any(node.ContainsKey))
        {
            Add(CompatibilitySeverity.Warning, CompatibilityIssueKind.MissingType, "type", "This schema has no type, which strict providers may refuse.", location);
        }

        if (capabilities.RequiresClosedObjects && IsType(node, "object") && node["additionalProperties"]?.GetValueKind() != JsonValueKind.False)
        {
            Add(CompatibilitySeverity.Error, CompatibilityIssueKind.OpenObject, "additionalProperties", $"{capabilities.Provider} needs additionalProperties: false.", location);
        }

        if (capabilities.RequiresAllProperties && node["properties"] is JsonObject properties)
        {
            int required = node["required"] is JsonArray names ? names.Count : 0;

            if (required < properties.Count)
            {
                Add(CompatibilitySeverity.Error, CompatibilityIssueKind.OptionalProperty, "required", $"{capabilities.Provider} needs every property in required.", location);
            }
        }
    }

    private void CheckPattern(JsonObject node, string location)
    {
        if (capabilities.MaxQuantifierBound is { } limit
            && LocalReferences.AsString(node["pattern"]) is { } pattern
            && RegexScanner.Scan(pattern).LargestBound > limit)
        {
            Add(CompatibilitySeverity.Warning, CompatibilityIssueKind.RegexComplexity, "pattern", $"A quantifier above {limit.ToString(CultureInfo.InvariantCulture)} may be too complex for {capabilities.Provider}.", location);
        }
    }

    private void Count(JsonObject node, string location, Counters counters)
    {
        if (node["properties"] is JsonObject properties)
        {
            HashSet<string?> required = node["required"] is JsonArray names ? [.. names.Select(LocalReferences.AsString)] : [];
            counters.Properties += properties.Count;
            counters.Optional += properties.Count(property => !required.Contains(property.Key));
            counters.Unions += properties.Count(property => IsUnion(property.Value));
            counters.Text += properties.Sum(property => property.Key.Length);
        }

        if (node["$defs"] is JsonObject definitions)
        {
            counters.Text += definitions.Sum(definition => definition.Key.Length);
        }

        if (node["const"] is JsonValue constant && constant.GetValueKind() == JsonValueKind.String)
        {
            counters.Text += constant.GetValue<string>().Length;
        }

        if (node["enum"] is JsonArray values)
        {
            int length = values.Sum(value => value?.GetValueKind() == JsonValueKind.String ? value.GetValue<string>().Length : 0);
            counters.EnumValues += values.Count;
            counters.Text += length;

            if (capabilities.LongEnumThreshold is { } threshold && capabilities.MaxLongEnumLength is { } limit && values.Count > threshold && length > limit)
            {
                Add(CompatibilitySeverity.Error, CompatibilityIssueKind.LimitExceeded, "enum", $"An enum with over {threshold.ToString(CultureInfo.InvariantCulture)} values can hold {limit.ToString(CultureInfo.InvariantCulture)} characters.", location);
            }
        }
    }

    private void CheckLimits(Counters counters)
    {
        void Check(int value, int? limit, string keyword, string what)
        {
            if (limit is { } maximum && value > maximum)
            {
                Add(CompatibilitySeverity.Error, CompatibilityIssueKind.LimitExceeded, keyword, $"{capabilities.Provider} allows {maximum.ToString(CultureInfo.InvariantCulture)} {what}, and this schema has {value.ToString(CultureInfo.InvariantCulture)}.");
            }
        }

        Check(counters.Properties, capabilities.MaxProperties, "properties", "object properties");
        Check(counters.Depth, capabilities.MaxNestingDepth, "properties", "levels of nesting");
        Check(counters.EnumValues, capabilities.MaxEnumValues, "enum", "enum values");
        Check(counters.Text, capabilities.MaxTotalStringLength, "properties", "characters of names and values");
        Check(counters.Optional, capabilities.MaxOptionalProperties, "required", "optional properties");
        Check(counters.Unions, capabilities.MaxUnionProperties, "anyOf", "union properties");
    }

    private sealed class Counters
    {
        public int Properties { get; set; }

        public int Depth { get; set; }

        public int EnumValues { get; set; }

        public int Text { get; set; }

        public int Optional { get; set; }

        public int Unions { get; set; }
    }
}
