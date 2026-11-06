using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Zorg.Json.Import;

namespace Zorg.Json.ComplianceTests.Fuzzing;

/// <summary>
/// Builds documents that are mostly references: a random tree of small schemas, each of which tells values apart, with
/// <c>$ref</c> planted on some of them, aimed at any other place in the tree. Places include keywords the dialect doesn't
/// read, the siblings of a <c>$ref</c>, unknown keywords, <c>$id</c> resources and anchors, spelled with percent and tilde escapes.
/// </summary>
internal sealed class ReferenceDocumentGenerator(Random random)
{
    private static readonly string[] Names = ["a", "b", "c", "d", "a/b", "t~x", "sp ace", "pc%t", "é", "$defs", "items", "0", "properties"];

    private static readonly string[] PatternNames = ["^a", "b$", "^c+$"];

    private static readonly string[] Singles = ["additionalProperties", "propertyNames", "contains", "not", "if", "then", "else", "items", "additionalItems", "contentSchema"];

    private static readonly string[] Lists = ["allOf", "anyOf", "oneOf", "prefixItems"];

    private static readonly string[] Maps = ["properties", "patternProperties", "$defs", "definitions", "dependentSchemas", "dependencies"];

    private readonly List<Place> _places = [];
    private int _serial;

    /// <summary>A subschema, with the root of the resource it's in. A pointer is read from there.</summary>
    private sealed record Place(JsonObject Node, IReadOnlyList<string> Tokens, IReadOnlyList<string> Resource, string? Anchor);

    public JsonSchemaImportDialect Dialect { get; private set; }

    private bool IsLegacy => Dialect != JsonSchemaImportDialect.Draft202012;

    private string IdKeyword => Dialect == JsonSchemaImportDialect.Draft04 ? "id" : "$id";

    public JsonObject Root(JsonSchemaImportDialect? dialect = null)
    {
        Dialect = dialect ?? random.Next(10) switch
        {
            < 5 => JsonSchemaImportDialect.Draft202012,
            < 7 => JsonSchemaImportDialect.Draft07,
            7 => JsonSchemaImportDialect.Draft06,
            8 => JsonSchemaImportDialect.Draft04,
            _ => JsonSchemaImportDialect.OpenApi30,
        };

        JsonObject root = [];
        Fill(root, [], [], depth: 0);

        if (random.Next(4) == 0 && Dialect != JsonSchemaImportDialect.OpenApi30)
        {
            root[IdKeyword] = $"http://example.com/root{random.Next(3)}.json";
        }

        AddDefinitions(root);
        Stash(root);
        PlantReferences(root);
        return root;
    }

    private void AddDefinitions(JsonObject root)
    {
        string native = IsLegacy ? "definitions" : "$defs";
        string keyword = random.Next(6) == 0 ? (IsLegacy ? "$defs" : "definitions") : native;
        JsonObject definitions = root[keyword] as JsonObject ?? [];
        root[keyword] = definitions;

        for (int index = random.Next(1, 4); index > 0; index--)
        {
            string name = Pick(Names);

            if (!definitions.ContainsKey(name))
            {
                definitions[name] = Child([keyword, name], [], depth: 2);
            }
        }
    }

    /// <summary>A place no dialect reads, which a pointer can still reach.</summary>
    private void Stash(JsonObject root)
    {
        if (random.Next(5) == 0)
        {
            root["x-stash"] = new JsonObject { ["k"] = Child(["x-stash", "k"], [], depth: 3) };
        }
    }

    private JsonObject Child(List<string> tokens, IReadOnlyList<string> resource, int depth)
    {
        JsonObject node = [];
        Fill(node, tokens, resource, depth);
        return node;
    }

    private void Fill(JsonObject node, List<string> tokens, IReadOnlyList<string> resource, int depth)
    {
        string? anchor = null;

        if (tokens.Count > 0 && random.Next(10) == 0 && Dialect != JsonSchemaImportDialect.OpenApi30)
        {
            resource = tokens;
            node[IdKeyword] = random.Next(2) == 0 ? $"sub{Next()}.json" : $"http://example.com/s{Next()}.json";
        }

        if (random.Next(10) == 0)
        {
            anchor = $"an{Next()}";

            if (!IsLegacy)
            {
                node["$anchor"] = anchor;
            }
            else if (Dialect != JsonSchemaImportDialect.OpenApi30 && !node.ContainsKey(IdKeyword))
            {
                node[IdKeyword] = "#" + anchor;
            }
            else
            {
                anchor = null;
            }
        }

        _places.Add(new Place(node, tokens, resource, anchor));

        if (depth >= 3 || (depth > 0 && random.Next(3) == 0))
        {
            Leaf(node);
            return;
        }

        int keywords = random.Next(1, 4);

        for (int index = 0; index < keywords; index++)
        {
            Keyword(node, tokens, resource, depth + 1);
        }

        if (random.Next(3) == 0)
        {
            Leaf(node);
        }
    }

    private void Keyword(JsonObject node, List<string> tokens, IReadOnlyList<string> resource, int depth)
    {
        int shape = random.Next(10);

        if (shape < 4)
        {
            string keyword = PickNative(Maps);

            if (node.ContainsKey(keyword))
            {
                return;
            }

            JsonObject map = [];
            node[keyword] = map;

            foreach (string name in Enumerable.Range(0, random.Next(1, 4)).Select(_ => keyword == "patternProperties" ? Pick(PatternNames) : Pick(Names)).Distinct())
            {
                map[name] = keyword == "dependencies" && random.Next(3) == 0
                    ? new JsonArray(Pick(Names))
                    : Child([.. tokens, keyword, name], resource, depth);

                // A list and a schema for one name are one value in draft-07.
                if (keyword == "dependentSchemas" && random.Next(3) == 0)
                {
                    node["dependentRequired"] = new JsonObject { [name] = new JsonArray(Pick(Names)) };
                }
            }
        }
        else if (shape < 7)
        {
            string keyword = PickNative(Lists);
            bool isTuple = keyword == "prefixItems" && IsLegacy;

            // Before 2020-12 a tuple is written as items, with additionalItems for the tail.
            keyword = isTuple && Dialect != JsonSchemaImportDialect.OpenApi30 ? "items" : keyword;

            if (node.ContainsKey(keyword))
            {
                return;
            }

            JsonArray list = [];
            node[keyword] = list;

            for (int index = random.Next(1, 4); index > 0; index--)
            {
                list.Add(Child([.. tokens, keyword, list.Count.ToString(CultureInfo.InvariantCulture)], resource, depth));
            }
        }
        else
        {
            string keyword = PickNative(Singles);

            if (!node.ContainsKey(keyword))
            {
                node[keyword] = Child([.. tokens, keyword], resource, depth);
            }

            if (keyword == "contains" && random.Next(3) == 0)
            {
                node["minContains"] = random.Next(2);
            }

            if (keyword == "contains" && random.Next(5) == 0)
            {
                node["maxContains"] = 2;
            }
        }
    }

    /// <summary>Mostly what the dialect reads, and now and then what it doesn't.</summary>
    private string PickNative(string[] keywords)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            string keyword = Pick(keywords);

            if (random.Next(6) == 0 || IsNative(keyword))
            {
                return keyword;
            }
        }

        return Pick(keywords);
    }

    private bool IsNative(string keyword) => keyword switch
    {
        "$defs" or "dependentSchemas" or "prefixItems" or "contentSchema" => !IsLegacy,
        "definitions" or "dependencies" or "additionalItems" => IsLegacy,
        "if" or "then" or "else" => Dialect is JsonSchemaImportDialect.Draft202012 or JsonSchemaImportDialect.Draft07,
        "contains" or "propertyNames" => Dialect is not (JsonSchemaImportDialect.Draft04 or JsonSchemaImportDialect.OpenApi30),
        "patternProperties" => Dialect != JsonSchemaImportDialect.OpenApi30,
        _ => true,
    };

    /// <summary>A rule that tells values apart, so that a reference aimed at the wrong place changes a verdict.</summary>
    private void Leaf(JsonObject node)
    {
        switch (random.Next(13))
        {
            case 0:
                node["type"] = "string";
                break;
            case 1:
                node["type"] = "integer";
                break;
            case 2:
                node["type"] = "object";
                break;
            case 3:
                node["type"] = "array";
                break;
            case 4:
                node["minLength"] = 2;
                break;
            case 5:
                node["maximum"] = 5;
                break;
            case 6:
                node["required"] = new JsonArray(Pick(Names));
                break;
            case 7:
                node["enum"] = new JsonArray("a", "bb", 1);
                break;
            case 8:
                node["maxItems"] = 1;
                break;
            case 9:
                node["pattern"] = "^[ab]+$";
                break;
            case 10:
                node["format"] = "date";
                break;
            case 11 when Dialect is not (JsonSchemaImportDialect.Draft04 or JsonSchemaImportDialect.OpenApi30):
                node["const"] = "bb";
                break;
            default:
                node["minProperties"] = 1;
                break;
        }
    }

    private void PlantReferences(JsonObject root)
    {
        Place[] sites = [.. _places.Where(place => place.Tokens.Count > 0)];

        if (sites.Length == 0)
        {
            return;
        }

        for (int index = random.Next(1, 6); index > 0; index--)
        {
            Place site = sites[random.Next(sites.Length)];
            Place[] reachable = [.. _places.Where(place => place.Resource.SequenceEqual(site.Resource))];
            string target = Aim(reachable[random.Next(reachable.Length)], site.Resource);

            // An even chance of keeping what's there as a sibling, which a legacy dialect ignores.
            if (random.Next(2) == 0)
            {
                foreach (string keyword in site.Node.Select(entry => entry.Key).Where(keyword => keyword is not ("$id" or "id" or "$anchor")).ToArray())
                {
                    site.Node.Remove(keyword);
                }
            }

            site.Node["$ref"] = target;
        }

        // A chain: a definition that is only a reference, and a reference to it.
        if (random.Next(3) == 0 && root[IsLegacy ? "definitions" : "$defs"] is JsonObject definitions)
        {
            Place[] top = [.. _places.Where(place => place.Resource.Count == 0)];
            definitions["chain"] = new JsonObject { ["$ref"] = Aim(top[random.Next(top.Length)], []) };
            (root["properties"] as JsonObject ?? (JsonObject)(root["properties"] = new JsonObject()))["via"] =
                new JsonObject { ["$ref"] = $"#/{(IsLegacy ? "definitions" : "$defs")}/chain" };
        }

        // A document that is only a reference, which a name can't take for a Z.Lazy.
        if (random.Next(8) == 0 && root[IsLegacy ? "definitions" : "$defs"] is JsonObject { Count: > 0 } named)
        {
            root["$ref"] = $"#/{(IsLegacy ? "definitions" : "$defs")}/{Spell(named.First().Key)}";
        }

        // A reference to what isn't a schema at all, such as a map of properties.
        if (random.Next(30) == 0 && root.ContainsKey("properties"))
        {
            sites[random.Next(sites.Length)].Node["$ref"] = "#/properties";
        }
    }

    private string Aim(Place target, IReadOnlyList<string> resource)
    {
        if (target.Anchor is not null && random.Next(2) == 0)
        {
            return "#" + target.Anchor;
        }

        return "#" + string.Concat(target.Tokens.Skip(resource.Count).Select(token => "/" + Spell(token)));
    }

    /// <summary>One token of a pointer, in a URI fragment. Tilde escapes come first, then percent escapes, in one of the ways a document may write them.</summary>
    private string Spell(string token)
    {
        string escaped = token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
        bool loose = random.Next(4) == 0;
        StringBuilder text = new();

        foreach (byte value in Encoding.UTF8.GetBytes(escaped))
        {
            char character = (char)value;
            bool isPlain = value < 0x80 && (char.IsAsciiLetterOrDigit(character) || "-._~$".Contains(character, StringComparison.Ordinal));

            if (isPlain && !(loose && character is '$' or '~'))
            {
                text.Append(character);
            }
            else
            {
                text.Append('%').Append(value.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return text.ToString();
    }

    private string Pick(string[] names) => names[random.Next(names.Length)];

    private int Next() => ++_serial;
}
