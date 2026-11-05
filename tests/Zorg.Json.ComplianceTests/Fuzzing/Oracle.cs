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
    public static bool? Accepts(JsonSchema schema, JsonElement instance)
    {
        if (!IsSafeForOracle(instance.GetRawText()))
        {
            return null;
        }

        try
        {
            return schema.Evaluate(instance).IsValid;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>JsonSchema.Net overflows the stack in its multipleOf check on numbers with huge exponents or digit runs, which ends the process.</summary>
    private static bool IsSafeForOracle(string raw) => !UnsafeNumber().IsMatch(raw);

    [GeneratedRegex(@"[eE][+-]?\d{3,}|\d{40,}")]
    private static partial Regex UnsafeNumber();

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
