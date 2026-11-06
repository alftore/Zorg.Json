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

        if (!Capabilities.Keywords.Contains("$anchor"))
        {
            ResolveAnchors(schema);
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

        // The emitter gives what a reference reaches a home under $defs, so a name in both is only a clash if something still reads this one.
        if (target is not null && definitions.Any(entry => target.ContainsKey(entry.Key)) && ReadsDefinitions(schema))
        {
            return;
        }

        int index = schema.IndexOf("definitions");
        schema.Remove("definitions");

        if (target is null)
        {
            target = [];
            schema.Insert(index, "$defs", target);
        }

        foreach ((string name, JsonNode? value) in definitions.ToArray())
        {
            definitions.Remove(name);
            target.TryAdd(name, value);
        }

        RewriteReferences(schema, reference => reference.StartsWith(Old, StringComparison.Ordinal) ? string.Concat(New, reference.AsSpan(Old.Length)) : null);
    }

    private static bool ReadsDefinitions(JsonObject schema)
    {
        bool found = false;
        JsonSchemaWalker.Walk(schema, (node, _, _) => found |= LocalReferences.AsString(node["$ref"])?.StartsWith("#/definitions/", StringComparison.Ordinal) == true);
        return found;
    }

    private static void ResolveAnchors(JsonObject schema)
    {
        Dictionary<string, string> anchors = new(StringComparer.Ordinal);

        JsonSchemaWalker.Walk(schema, (node, location, _) =>
        {
            if (LocalReferences.AsString(node["$anchor"]) is { } name)
            {
                anchors.TryAdd(name, location);
            }
        });

        RewriteReferences(
            schema,
            reference => reference.Length > 1 && reference[0] == '#' && reference[1] != '/' && anchors.TryGetValue(reference[1..], out string? location)
                ? $"#{location}"
                : null);
    }

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
