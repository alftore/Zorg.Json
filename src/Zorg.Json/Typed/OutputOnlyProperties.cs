using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zorg.Json.Model;

namespace Zorg.Json.Typed;

/// <summary>
/// Computed properties are serialized but never accepted as input, so an instance that is serialized to be validated
/// has to lose them first, or its own JSON would be rejected as having unknown keys.
/// </summary>
internal static class OutputOnlyProperties
{
    public static bool Any(SchemaNode node) => Any(node, new(ReferenceEqualityComparer.Instance));

    public static JsonElement Remove(JsonElement element, SchemaNode node)
    {
        JsonNode? root = JsonNode.Parse(element.GetRawText());
        Strip(root, node);
        return JsonElement.Parse(root?.ToJsonString() ?? "null");
    }

    private static bool Any(SchemaNode node, HashSet<object> visited) =>
        visited.Add(node)
        && (node.Properties.Any(property => property.OutputOnly)
            || node.Children.Any(child => Any(child, visited))
            || (node.Behavior.Target is { } target && Any(target(), visited)));

    private static void Strip(JsonNode? value, SchemaNode node)
    {
        if (node.Behavior.Target is { } target)
        {
            node = target();
        }

        if (value is JsonArray array && node.Items is { } items)
        {
            foreach (JsonNode? item in array)
            {
                Strip(item, items);
            }
        }
        else if (value is JsonObject obj)
        {
            foreach (SchemaProperty property in node.Properties.Where(property => property.OutputOnly))
            {
                obj.Remove(property.Name);
            }

            foreach ((string name, JsonNode? child) in obj.ToArray())
            {
                SchemaNode? schema = node.Properties.FirstOrDefault(property => property.Name == name)?.Schema ?? node.AdditionalProperties;

                if (schema is not null)
                {
                    Strip(child, schema);
                }
            }

            StripBranch(obj, node);
        }
    }

    /// <summary>A union is read through its discriminator, the way System.Text.Json writes it.</summary>
    private static void StripBranch(JsonObject obj, SchemaNode node)
    {
        if (node.Behavior.Discriminator is not { } discriminator || obj[discriminator] is not JsonValue key)
        {
            return;
        }

        string text = key.ToJsonString();

        foreach (SchemaNode branch in node.OneOf)
        {
            SchemaNode resolved = branch.Behavior.Target is { } target ? target() : branch;

            if (resolved.Properties.FirstOrDefault(property => property.Name == discriminator)?.Schema.Const is { } constant
                && constant.GetRawText() == text)
            {
                Strip(obj, resolved);
            }
        }
    }
}
