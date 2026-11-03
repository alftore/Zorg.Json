using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Zorg.Json.Model;

namespace Zorg.Json.Validation;

/// <summary>
/// Works out whether a <c>oneOf</c> can be selected by one property. It can when every branch requires the property
/// with its own values, so that at most one branch can ever match. Otherwise the union behaves like a plain <c>oneOf</c>.
/// </summary>
internal static class DiscriminatorKeys
{
    public static bool TryBuild(SchemaNode node, out Dictionary<string, SchemaNode> branches)
    {
        branches = new(StringComparer.Ordinal);

        if (node.Behavior.Discriminator is not { } discriminator || node.OneOf.IsEmpty)
        {
            return false;
        }

        foreach (SchemaNode branch in node.OneOf)
        {
            SchemaNode resolved = Resolve(branch);
            SchemaProperty? property = resolved.Properties.FirstOrDefault(candidate => candidate.Name == discriminator);

            if (property is null || !resolved.Required.Contains(discriminator)
                || !property.Schema.Behavior.Normalizers.IsEmpty || property.Schema.Behavior.Coercion != SchemaCoercion.None)
            {
                return false;
            }

            JsonElement[] values = property.Schema.Const is { } constant ? [constant] : [.. property.Schema.Enum];

            if (values.Length == 0)
            {
                return false;
            }

            foreach (JsonElement value in values)
            {
                if (Key(value) is not { } key || !branches.TryAdd(key, branch))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Strings are prefixed, so that the string <c>"1"</c> and the number <c>1</c> stay apart.</summary>
    public static string? Key(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return $"#{value.GetRawText()}";
        }

        try
        {
            return $"${value.GetString()}";
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static SchemaNode Resolve(SchemaNode node)
    {
        SchemaNode current = node;

        for (int hops = 0; hops < 64 && current.Behavior.Target is { } target; hops++)
        {
            current = target();
        }

        return current;
    }
}
