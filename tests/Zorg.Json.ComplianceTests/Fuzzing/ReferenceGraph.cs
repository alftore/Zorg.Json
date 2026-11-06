using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Zorg.Json.Emission;

namespace Zorg.Json.ComplianceTests.Fuzzing;

/// <summary>
/// Reads an emitted document as a validator of its dialect would, without any of the emitter's code: which places are
/// schemas, which resource each belongs to, and where each <c>$ref</c> lands.
/// </summary>
internal sealed class ReferenceGraph
{
    private static readonly HashSet<string> Singles = new(StringComparer.Ordinal)
    {
        "additionalProperties", "not", "items",
    };

    private static readonly HashSet<string> LaterSingles = new(StringComparer.Ordinal)
    {
        "propertyNames", "contains", "if", "then", "else",
    };

    private readonly JsonSchemaDialect _dialect;
    private readonly JsonNode _root;
    private readonly HashSet<string> _schemas = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> _resources = new(StringComparer.Ordinal);
    private readonly HashSet<(string Resource, string Anchor)> _anchors = [];
    private readonly List<(string Reference, string Resource, string Pointer)> _references = [];
    private readonly List<string> _ids = [];
    private readonly Dictionary<string, string> _bases = new(StringComparer.Ordinal);
    private readonly List<string> _duplicateAnchors = [];

    private ReferenceGraph(JsonNode root, JsonSchemaDialect dialect)
    {
        _root = root;
        _dialect = dialect;
    }

    private bool IsLegacyRef => _dialect is JsonSchemaDialect.Draft07 or JsonSchemaDialect.OpenApi30;

    private bool HasIds => _dialect != JsonSchemaDialect.OpenApi30;

    private bool IsDraft07 => _dialect == JsonSchemaDialect.Draft07;

    private bool IsModern => _dialect is JsonSchemaDialect.Draft202012 or JsonSchemaDialect.OpenApi31;

    /// <summary>Every broken promise about references in the document, as kind and detail. A <paramref name="container"/> such as <c>components</c> holds schemas that no dialect names.</summary>
    public static List<(string Kind, string Detail)> Check(JsonObject root, JsonSchemaDialect dialect, string? container = null)
    {
        ReferenceGraph graph = new(root, dialect);
        graph._resources[string.Empty] = root;
        graph.WalkSchema(root, string.Empty, string.Empty, container);
        return graph.Judge();
    }

    private void WalkSchema(JsonNode? node, string pointer, string resource, string? container = null)
    {
        _schemas.Add(pointer);

        if (node is not JsonObject schema)
        {
            return;
        }

        if (HasIds && schema["$id"] is JsonValue id && id.TryGetValue(out string? text))
        {
            int fragment = text.IndexOf('#', StringComparison.Ordinal);

            if (fragment != 0 && !(IsLegacyRef && schema.ContainsKey("$ref")))
            {
                string baseUri = _bases.GetValueOrDefault(resource, "http://document.invalid/");
                string uri = Uri.TryCreate(new Uri(baseUri), text.TrimEnd('#'), out Uri? resolved) ? resolved.AbsoluteUri : text.TrimEnd('#');
                _ids.Add(uri);
                _bases[pointer] = uri;
            }

            if (fragment != 0 && !(IsLegacyRef && schema.ContainsKey("$ref")) && pointer.Length > 0)
            {
                resource = pointer;
                _resources[pointer] = schema;
            }

            if (IsDraft07 && fragment >= 0 && fragment < text.Length - 1)
            {
                Anchor(resource, text[(fragment + 1)..]);
            }
        }

        if (IsModern && schema["$anchor"] is JsonValue anchor && anchor.TryGetValue(out string? name))
        {
            Anchor(resource, name);
        }

        if (schema["$ref"] is JsonValue reference && reference.TryGetValue(out string? target))
        {
            _references.Add((target, resource, pointer));
        }

        foreach ((string keyword, JsonNode? value) in schema)
        {
            // Before 2019-09 everything beside $ref is ignored, but a container of definitions can still be pointed into.
            if (IsLegacyRef && schema.ContainsKey("$ref") && keyword is not ("definitions" or "$defs"))
            {
                continue;
            }

            WalkKeyword(keyword, value, $"{pointer}/{Token(keyword)}", resource);
        }

        if (container is not null && schema["components"]?["schemas"] is JsonObject definitions)
        {
            foreach ((string name2, JsonNode? definition) in definitions)
            {
                WalkSchema(definition, $"/components/schemas/{Token(name2)}", resource);
            }
        }
    }

    private void Anchor(string resource, string name)
    {
        if (!_anchors.Add((resource, name)))
        {
            _duplicateAnchors.Add(name);
        }
    }

    private void WalkKeyword(string keyword, JsonNode? value, string pointer, string resource)
    {
        bool isOpenApi30 = _dialect == JsonSchemaDialect.OpenApi30;

        if (Singles.Contains(keyword) && !(IsDraft07 && keyword == "items" && value is JsonArray))
        {
            WalkSchema(value, pointer, resource);
        }
        else if (!isOpenApi30 && LaterSingles.Contains(keyword))
        {
            WalkSchema(value, pointer, resource);
        }
        else if (IsDraft07 && keyword == "additionalItems")
        {
            WalkSchema(value, pointer, resource);
        }
        else if (IsModern && keyword is "contentSchema" or "unevaluatedItems" or "unevaluatedProperties")
        {
            WalkSchema(value, pointer, resource);
        }
        else if (keyword is "allOf" or "anyOf" or "oneOf" || (IsModern && keyword == "prefixItems") || (IsDraft07 && keyword == "items"))
        {
            WalkList(value as JsonArray, pointer, resource);
        }
        else if (keyword == "properties" || (!isOpenApi30 && keyword == "patternProperties"))
        {
            WalkMap(value as JsonObject, pointer, resource);
        }
        else if ((IsDraft07 && keyword is "definitions" or "dependencies") || (!IsDraft07 && keyword == "$defs") || (IsModern && keyword == "dependentSchemas"))
        {
            WalkMap(value as JsonObject, pointer, resource);
        }
    }

    private void WalkList(JsonArray? list, string pointer, string resource)
    {
        for (int index = 0; list is not null && index < list.Count; index++)
        {
            WalkSchema(list[index], $"{pointer}/{index.ToString(CultureInfo.InvariantCulture)}", resource);
        }
    }

    private void WalkMap(JsonObject? map, string pointer, string resource)
    {
        foreach ((string name, JsonNode? value) in map ?? [])
        {
            // A dependency may be a list of names, which isn't a schema.
            if (value is not JsonArray)
            {
                WalkSchema(value, $"{pointer}/{Token(name)}", resource);
            }
        }
    }

    private static string Token(string name) => name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private List<(string Kind, string Detail)> Judge()
    {
        List<(string Kind, string Detail)> found = [];

        foreach (IGrouping<string, string> id in _ids.GroupBy(id => id, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            found.Add(("a-duplicate-id", $"$id {id.Key} appears {id.Count()} times"));
        }

        foreach (string anchor in _duplicateAnchors.Distinct(StringComparer.Ordinal))
        {
            found.Add(("a-duplicate-anchor", $"$anchor {anchor} appears twice in one resource"));
        }

        foreach ((string reference, string resource, string pointer) in _references)
        {
            if (Land(reference, resource) is { } kind)
            {
                found.Add((kind, $"$ref {reference} at {pointer} in resource \"{resource}\""));
            }
        }

        return found;
    }

    /// <summary>What's wrong with where the reference lands, or null when it lands on a schema.</summary>
    private string? Land(string reference, string resource)
    {
        if (!reference.StartsWith('#'))
        {
            return "a-dangling";
        }

        string fragment;

        try
        {
            fragment = Uri.UnescapeDataString(reference[1..]);
        }
        catch (UriFormatException)
        {
            return "a-dangling";
        }

        if (fragment.Length == 0)
        {
            return null;
        }

        if (fragment[0] != '/')
        {
            return _anchors.Contains((resource, fragment)) ? null : "a-dangling";
        }

        JsonNode? current = resource.Length == 0 ? _root : _resources[resource];
        string walked = resource;
        bool isInert = false;

        foreach (string token in fragment[1..].Split('/'))
        {
            string name = token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            isInert |= name.StartsWith("x-inert-", StringComparison.Ordinal);

            current = current switch
            {
                JsonObject members when members.TryGetPropertyValue(name, out JsonNode? child) => child,
                JsonArray items when int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index < items.Count => items[index],
                _ => null,
            };

            if (current is null)
            {
                return "a-dangling";
            }

            walked += "/" + token;
        }

        if (_schemas.Contains(walked))
        {
            return null;
        }

        bool looksLikeSchema = current is JsonObject || (current is JsonValue value && value.TryGetValue(out bool _));
        return isInert || !looksLikeSchema ? "a-non-schema" : "a-unknown-place";
    }
}
