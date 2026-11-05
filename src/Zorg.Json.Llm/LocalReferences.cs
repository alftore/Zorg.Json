using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Zorg.Json.Emission;

namespace Zorg.Json.Llm;

/// <summary>Finds the <c>$ref</c> values of an emitted document that lead nowhere inside it.</summary>
internal static class LocalReferences
{
    private const string DanglingSuffix = "doesn't lead anywhere in the emitted schema.";

    public static string? AsString(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    public static string DanglingMessage(string reference) => $"The reference {reference} {DanglingSuffix}";

    public static bool IsDangling(EmitDiagnostic diagnostic) =>
        diagnostic.Keyword == "$ref" && diagnostic.Message.EndsWith(DanglingSuffix, StringComparison.Ordinal);

    public static List<(string Location, string Reference)> Dangling(JsonObject root)
    {
        HashSet<string> anchors = new(StringComparer.Ordinal);
        List<(string Location, string Reference)> found = [];

        JsonSchemaWalker.Walk(root, (node, _, _) =>
        {
            if (AsString(node["$anchor"]) is { } anchor)
            {
                anchors.Add(anchor);
            }
        });

        JsonSchemaWalker.Walk(root, (node, location, _) =>
        {
            if (AsString(node["$ref"]) is { } reference && !Resolves(root, reference, anchors))
            {
                found.Add((location, reference));
            }
        });

        return found;
    }

    private static bool Resolves(JsonObject root, string reference, HashSet<string> anchors)
    {
        if (!reference.StartsWith('#') || reference.Length == 1)
        {
            return true;
        }

        if (reference[1] != '/')
        {
            return anchors.Contains(reference[1..]);
        }

        JsonNode? current = root;

        foreach (string part in reference[2..].Split('/'))
        {
            string name = Uri.UnescapeDataString(part).Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

            current = current switch
            {
                JsonObject target => target.TryGetPropertyValue(name, out JsonNode? member) ? member : null,
                JsonArray list when int.TryParse(name, out int index) && index >= 0 && index < list.Count => list[index],
                _ => null,
            };

            if (current is null)
            {
                return false;
            }
        }

        return true;
    }
}
