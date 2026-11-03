using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zorg.Json.Emission;
using Zorg.Json.Model;

namespace Zorg.Json.Llm;

/// <summary>Turns JSON shaped by an emitted schema back into the shape the full schema expects.</summary>
internal sealed class ModelOutputAdapter(SchemaNode root)
{
    private const string DefsPrefix = "#/$defs/";

    public static JsonElement Adapt(SchemaNode root, EmitResult emitted, JsonElement json)
    {
        ImmutableArray<ISchemaRewriter> rewriters = emitted.Target?.Rewriters ?? [];
        bool requiredAll = rewriters.Contains(Rewriters.RequireAllAsNullable);
        bool unwrap = rewriters.Contains(Rewriters.RequireObjectRoot) && !Rewriters.HasObjectRoot(root) && IsWrapper(emitted.Schema);

        if (!unwrap && !requiredAll)
        {
            return json;
        }

        JsonNode? value = JsonNode.Parse(json.GetRawText());

        if (unwrap && value is JsonObject wrapper && wrapper.Count == 1 && wrapper.TryGetPropertyValue(Rewriters.RootWrapperProperty, out JsonNode? inner))
        {
            wrapper.Remove(Rewriters.RootWrapperProperty);
            value = inner;
        }

        if (requiredAll)
        {
            new ModelOutputAdapter(root).Visit(value, root);
        }

        return JsonElement.Parse(value?.ToJsonString() ?? "null");
    }

    private static bool IsWrapper(JsonObject schema) =>
        schema["properties"] is JsonObject { Count: 1 } properties && properties.ContainsKey(Rewriters.RootWrapperProperty);

    private static bool Matches(JsonNode? value, SchemaNode node) => value switch
    {
        JsonObject target => MatchesObject(target, node),
        JsonArray => node.Types == JsonTypes.None || node.Types.HasFlag(JsonTypes.Array),
        _ => false,
    };

    private static bool MatchesObject(JsonObject value, SchemaNode node)
    {
        if (node.Types != JsonTypes.None && !node.Types.HasFlag(JsonTypes.Object))
        {
            return false;
        }

        if (node.Required.Any(name => !value.ContainsKey(name)))
        {
            return false;
        }

        if (node.Behavior.UnknownProperties == UnknownProperties.Reject
            && node.AdditionalProperties is null
            && value.Any(entry => node.Properties.All(property => property.Name != entry.Key)))
        {
            return false;
        }

        return node.Properties.All(property => !value.TryGetPropertyValue(property.Name, out JsonNode? item) || HasAllowedValue(item, property.Schema));
    }

    private static bool HasAllowedValue(JsonNode? value, SchemaNode node)
    {
        JsonElement element = JsonElement.Parse(value?.ToJsonString() ?? "null");

        if (node.Const is { } constant && !JsonElement.DeepEquals(constant, element))
        {
            return element.ValueKind == JsonValueKind.Null;
        }

        return node.Enum.IsEmpty || element.ValueKind == JsonValueKind.Null || node.Enum.Any(item => JsonElement.DeepEquals(item, element));
    }

    private void Visit(JsonNode? value, SchemaNode schema)
    {
        SchemaNode? node = Resolve(schema);

        if (node is null)
        {
            return;
        }

        if (value is JsonObject target)
        {
            VisitProperties(target, node);
        }
        else if (value is JsonArray array)
        {
            VisitItems(array, node);
        }
        else
        {
            return;
        }

        foreach (SchemaNode branch in node.AllOf)
        {
            Visit(value, branch);
        }

        if (Select(value, node.AnyOf.IsEmpty ? node.OneOf : node.AnyOf) is { } selected)
        {
            Visit(value, selected);
        }
    }

    private void VisitProperties(JsonObject value, SchemaNode node)
    {
        foreach (SchemaProperty property in node.Properties)
        {
            if (!value.TryGetPropertyValue(property.Name, out JsonNode? item))
            {
                continue;
            }

            if (item is null && !node.Required.Contains(property.Name) && !AcceptsNull(property.Schema))
            {
                value.Remove(property.Name);
            }
            else
            {
                Visit(item, property.Schema);
            }
        }
    }

    private void VisitItems(JsonArray value, SchemaNode node)
    {
        for (int index = 0; index < value.Count; index++)
        {
            SchemaNode? item = index < node.PrefixItems.Length ? node.PrefixItems[index] : node.Items;

            if (item is not null)
            {
                Visit(value[index], item);
            }
        }
    }

    private SchemaNode? Select(JsonNode? value, ImmutableArray<SchemaNode> branches) =>
        branches.FirstOrDefault(branch => Resolve(branch) is { } resolved && Matches(value, resolved));

    private bool AcceptsNull(SchemaNode schema) => Resolve(schema) is { } node && Rewriters.AcceptsNull(node);

    private SchemaNode? Resolve(SchemaNode node)
    {
        const int MaxHops = 32;
        SchemaNode? current = node;

        for (int hop = 0; hop < MaxHops && current?.Ref is { } reference; hop++)
        {
            current = current.Behavior.Target is { } target ? target() : Lookup(reference);
        }

        return current?.Ref is null ? current : null;
    }

    private SchemaNode? Lookup(string reference)
    {
        if (reference == "#")
        {
            return root;
        }

        if (!reference.StartsWith(DefsPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        string name = reference[DefsPrefix.Length..].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
        return root.Defs.FirstOrDefault(definition => definition.Name == name)?.Schema;
    }
}
