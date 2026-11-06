using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zorg.Json.Emission;

namespace Zorg.Json.Llm;

/// <summary>
/// Carries a profile's table and handles what rewriters can't see: conditions on a value, and keywords that only appear
/// during emission, such as <c>format: int64</c> and the <c>pattern</c> of a coerced number.
/// </summary>
internal sealed class CapabilityEnforcer(SchemaCapabilities capabilities) : IJsonSchemaPostProcessor
{
    public SchemaCapabilities Capabilities { get; } = capabilities;

    public void Process(JsonObject schema, EmitContext context)
    {
        KeepReferencesValid(schema);
        JsonSchemaWalker.Walk(schema, (node, location, _) => Enforce(node, location, context));

        foreach ((string location, string reference) in LocalReferences.Dangling(schema))
        {
            context.Report(new EmitDiagnostic("$ref", LocalReferences.DanglingMessage(reference), location));
        }
    }

    private static string Text(JsonNode? value) =>
        value?.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value?.ToJsonString(JsonSchemaWalker.TextOptions) ?? "null";

    private static void Annotate(JsonObject node, string note)
    {
        if (LocalReferences.AsString(node["description"]) is { } existing)
        {
            node["description"] = $"{existing} {note}";
        }
        else if (node.ContainsKey("description"))
        {
            node["description"] = note;
        }
        else
        {
            int index = node.ContainsKey("title") ? node.IndexOf("title") + 1 : 0;
            node.Insert(index, "description", note);
        }
    }

    private static void RewriteReferences(JsonObject schema, Func<string, string?> rewrite) =>
        JsonSchemaWalker.Walk(schema, (node, _, _) =>
        {
            if (LocalReferences.AsString(node["$ref"]) is { } reference && rewrite(reference) is { } replacement)
            {
                node["$ref"] = replacement;
            }
        });

    // Moving a keyword to description must not leave a reference pointing at it.
    private void KeepReferencesValid(JsonObject schema)
    {
        if (!Capabilities.Keywords.Contains("definitions") && Capabilities.Keywords.Contains("$defs"))
        {
            RenameDefinitions(schema);
        }

        bool keepsIds = Capabilities.Keywords.Contains("$id");

        if (!keepsIds || !Capabilities.Keywords.Contains("$anchor"))
        {
            FlattenReferences(schema, keepsIds);
        }
    }

    private static void RenameDefinitions(JsonObject schema)
    {
        const string Old = "#/definitions/";
        const string New = "#/$defs/";

        if (schema["definitions"] is not JsonObject definitions || (schema.ContainsKey("$defs") && schema["$defs"] is not JsonObject))
        {
            return;
        }

        JsonObject? target = schema["$defs"] as JsonObject;

        int index = schema.IndexOf("definitions");
        schema.Remove("definitions");

        // The emitter gives what a reference reaches a home under $defs. What nothing outside this text reads is no rule, and as a schema its own references could dangle.
        if (!ReadsDefinitions(schema))
        {
            return;
        }

        if (target is not null && definitions.Any(entry => target.ContainsKey(entry.Key)))
        {
            schema.Insert(index, "definitions", definitions);
            return;
        }

        if (target is null)
        {
            target = [];
            schema.Insert(index, "$defs", target);
        }

        foreach ((string name, JsonNode? value) in definitions.ToArray())
        {
            definitions.Remove(name);
            target.Add(name, value);
        }

        RewriteReferences(schema, reference => reference.StartsWith(Old, StringComparison.Ordinal) ? string.Concat(New, reference.AsSpan(Old.Length)) : null);
    }

    private static bool ReadsDefinitions(JsonObject schema)
    {
        bool found = false;
        JsonSchemaWalker.Walk(schema, (node, _, _) => found |= LocalReferences.AsString(node["$ref"])?.StartsWith("#/definitions/", StringComparison.Ordinal) == true);
        return found;
    }

    /// <summary>
    /// Writes anchors as pointers. A reference is read from its resource, and a provider without <c>$id</c> reads the whole
    /// document as one, so there a pointer inside a resource has to start at the document.
    /// </summary>
    private static void FlattenReferences(JsonObject schema, bool keepsIds)
    {
        HashSet<string> resources = new(StringComparer.Ordinal) { string.Empty };
        Dictionary<(string Resource, string Name), string> anchors = [];

        JsonSchemaWalker.Walk(schema, (node, location, _) =>
        {
            if (location.Length > 0 && LocalReferences.AsString(node["$id"]) is { } id && !id.StartsWith('#'))
            {
                resources.Add(location);
            }
        });

        string Resource(string location)
        {
            string current = location;

            while (!resources.Contains(current))
            {
                current = current[..current.LastIndexOf('/')];
            }

            return current;
        }

        JsonSchemaWalker.Walk(schema, (node, location, _) =>
        {
            if (LocalReferences.AsString(node["$anchor"]) is { } name)
            {
                anchors.TryAdd((Resource(location), name), location);
            }
        });

        JsonSchemaWalker.Walk(schema, (node, location, _) =>
        {
            if (LocalReferences.AsString(node["$ref"]) is not { } reference || !reference.StartsWith('#'))
            {
                return;
            }

            string resource = Resource(location);

            if (reference.Length > 1 && reference[1] != '/')
            {
                if (anchors.TryGetValue((resource, Uri.UnescapeDataString(reference[1..])), out string? place))
                {
                    node["$ref"] = "#" + Fragment(keepsIds ? place[resource.Length..] : place);
                }
            }
            else if (!keepsIds && resource.Length > 0)
            {
                node["$ref"] = "#" + Fragment(resource) + reference[1..];
            }
        });
    }

    private static string Fragment(string pointer) =>
        string.Join('/', pointer.Split('/').Select(token => Uri.EscapeDataString(token).Replace("%24", "$", StringComparison.Ordinal).Replace("%7E", "~", StringComparison.Ordinal)));

    private void Enforce(JsonObject node, string location, EmitContext context)
    {
        List<string> moved = [];

        foreach ((string keyword, JsonNode? value) in node.ToArray())
        {
            KeywordVerdict verdict = CapabilityRules.Check(Capabilities, keyword, value);

            if (verdict == KeywordVerdict.Allowed)
            {
                continue;
            }

            node.Remove(keyword);

            if (verdict == KeywordVerdict.Implied)
            {
                continue;
            }

            context.Report(new EmitDiagnostic(keyword, $"{context.Options.Target.Name} can't state this {keyword}.", location));

            if (verdict == KeywordVerdict.Relocate)
            {
                moved.Add($"{keyword} {Text(value)}");
            }
        }

        if (moved.Count > 0 && context.Options.Unrepresentable == UnrepresentablePolicy.Annotate)
        {
            Annotate(node, $"Must also satisfy: {string.Join("; ", moved)}.");
        }
    }
}
