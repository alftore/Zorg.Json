using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Schema;

namespace Zorg.Json.ComplianceTests.Fuzzing;

/// <summary>JsonSchema.Net, wrapped so that its own failures count as "no opinion" instead of failing a run.</summary>
internal static partial class Oracle
{
    /// <summary>
    /// A registry of its own for each schema. The global one keeps every <c>$id</c> it has seen, so across thousands of
    /// iterations in a long run a <c>$ref</c> could resolve to another iteration's schema and the oracle would judge the wrong one.
    /// </summary>
    private static BuildOptions Fresh() => new() { SchemaRegistry = new SchemaRegistry() };

    /// <summary>Null when JsonSchema.Net can't load the schema.</summary>
    public static JsonSchema? TryBuild(JsonObject schema)
    {
        try
        {
            RemoveInertKeywords(schema);
            RemoveUnusedDefinitions(schema);

            // OpenAPI 3.1 output has no $schema, and without one JsonSchema.Net refuses every unknown keyword.
            if (!schema.ContainsKey("$schema"))
            {
                schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
            }

            return JsonSchema.FromText(schema.ToJsonString(), Fresh());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    public static JsonSchema? TryBuild(string text)
    {
        try
        {
            return JsonSchema.FromText(text, Fresh());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Null when JsonSchema.Net throws on the instance.</summary>
    public static bool? Accepts(JsonSchema schema, JsonElement instance, bool usesMultipleOf, bool assertFormat = false)
    {
        if (!IsSafeForOracle(instance.GetRawText(), usesMultipleOf))
        {
            return null;
        }

        try
        {
            return schema.Evaluate(instance, new EvaluationOptions { RequireFormatValidation = assertFormat }).IsValid;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// JsonSchema.Net overflows the stack in its multipleOf check, which ends the process. Measured: 1e-1000000 overflows
    /// and 1e-100000 doesn't, so five exponent digits leaves a margin. No other keyword crashes it, and digit runs don't.
    /// </summary>
    public static bool IsSafeForOracle(string raw, bool usesMultipleOf) => !usesMultipleOf || !UnsafeNumber().IsMatch(raw);

    [GeneratedRegex(@"[eE][+-]?\d{5,}")]
    private static partial Regex UnsafeNumber();

    /// <summary>JsonSchema.Net rounds numbers a double can't hold: to it, 4.9e-325 equals 0. Zorg compares them exactly, so a rejection of one proves nothing.</summary>
    public static bool IsBeyondItsPrecision(string raw) => BeyondPrecision().IsMatch(raw);

    [GeneratedRegex(@"[eE][+-]?\d{3,}|\d{16,}|\.\d*0{15,}")]
    private static partial Regex BeyondPrecision();

    /// <summary>
    /// Draft-07 output keeps <c>definitions</c> as written, even invalid ones nothing refers to, and JsonSchema.Net refuses
    /// to load a document with an invalid subschema anywhere in it. Nothing can reach these, so removing them changes nothing.
    /// </summary>
    private static void RemoveUnusedDefinitions(JsonObject schema)
    {
        if (schema["definitions"] is not JsonObject definitions)
        {
            return;
        }

        string text = schema.ToJsonString();

        foreach (string name in definitions.Select(entry => entry.Key).ToArray())
        {
            if (!text.Contains("#/definitions/" + name, StringComparison.Ordinal) && definitions[name]?.ToJsonString().Contains("\"$id\"", StringComparison.Ordinal) != true)
            {
                definitions.Remove(name);
            }
        }
    }

    private static void RemoveInertKeywords(JsonNode? node)
    {
        if (node is JsonObject schema)
        {
            foreach (string keyword in schema.Select(entry => entry.Key).Where(key => key.StartsWith("x-inert-", StringComparison.Ordinal)).ToArray())
            {
                schema.Remove(keyword);
            }

            foreach ((string _, JsonNode? child) in schema)
            {
                RemoveInertKeywords(child);
            }
        }
        else if (node is JsonArray list)
        {
            foreach (JsonNode? child in list)
            {
                RemoveInertKeywords(child);
            }
        }
    }
}
