using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zorg.Json.Llm;

/// <summary>Visits every object-form subschema of an emitted document, children first.</summary>
internal static class JsonSchemaWalker
{
    public static readonly JsonSerializerOptions TextOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly string[] Single =
        ["items", "contains", "additionalProperties", "propertyNames", "not", "if", "then", "else", "unevaluatedItems", "unevaluatedProperties", "contentSchema"];

    private static readonly string[] Lists = ["prefixItems", "allOf", "anyOf", "oneOf"];

    private static readonly string[] Maps = ["properties", "patternProperties", "dependentSchemas", "$defs", "definitions"];

    /// <param name="root">The document.</param>
    /// <param name="visit">Gets the subschema, its JSON Pointer, and how many objects with <c>properties</c> enclose it, itself included.</param>
    public static void Walk(JsonObject root, Action<JsonObject, string, int> visit) => Walk(root, string.Empty, 0, visit);

    private static void Walk(JsonObject schema, string location, int depth, Action<JsonObject, string, int> visit)
    {
        int level = schema["properties"] is JsonObject ? depth + 1 : depth;

        foreach (string keyword in Single)
        {
            Child(schema[keyword], $"{location}/{keyword}", level, visit);
        }

        foreach (string keyword in Lists)
        {
            if (schema[keyword] is JsonArray list)
            {
                for (int index = 0; index < list.Count; index++)
                {
                    Child(list[index], $"{location}/{keyword}/{index}", level, visit);
                }
            }
        }

        foreach (string keyword in Maps)
        {
            if (schema[keyword] is JsonObject map)
            {
                bool isDefinitions = keyword is "$defs" or "definitions";

                foreach (KeyValuePair<string, JsonNode?> entry in map.ToArray())
                {
                    Child(entry.Value, $"{location}/{keyword}/{Escape(entry.Key)}", isDefinitions ? 0 : level, visit);
                }
            }
        }

        visit(schema, location, level);
    }

    private static void Child(JsonNode? node, string location, int depth, Action<JsonObject, string, int> visit)
    {
        if (node is JsonObject schema)
        {
            Walk(schema, location, depth, visit);
        }
    }

    private static string Escape(string name) =>
        name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
