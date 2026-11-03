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

    public void Process(JsonObject schema, EmitContext context) =>
        JsonSchemaWalker.Walk(schema, (node, location, _) => Enforce(node, location, context));

    private static string Text(JsonNode? value) =>
        value?.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value?.ToJsonString(JsonSchemaWalker.TextOptions) ?? "null";

    private static void Annotate(JsonObject node, string note)
    {
        if (node["description"] is JsonValue existing)
        {
            node["description"] = $"{existing.GetValue<string>()} {note}";
        }
        else
        {
            int index = node.ContainsKey("title") ? node.IndexOf("title") + 1 : 0;
            node.Insert(index, "description", note);
        }
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
