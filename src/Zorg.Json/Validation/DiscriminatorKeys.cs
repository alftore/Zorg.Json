using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Zorg.Json.Model;

namespace Zorg.Json.Validation;

/// <summary>
/// Works out whether a <c>oneOf</c> can be selected by one property. It can when every branch requires the property
/// with its own values, so that at most one branch can ever match. Otherwise the union behaves like a plain <c>oneOf</c>.
/// Values are compared as JSON values, so <c>1</c> and <c>1.0</c> are the same value, as they are for <c>oneOf</c>.
/// </summary>
internal static class DiscriminatorKeys
{
    public static bool TryBuild(SchemaNode node, out List<(JsonElement Value, SchemaNode Branch)> entries)
    {
        entries = [];
        Dictionary<int, List<JsonElement>> seen = [];

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
                int hash = JsonEquality.Hash(value);

                if (!seen.TryGetValue(hash, out List<JsonElement>? bucket))
                {
                    seen[hash] = bucket = [];
                }

                if (bucket.Exists(other => JsonEquality.AreEqual(other, value)))
                {
                    return false;
                }

                bucket.Add(value);
                entries.Add((value, branch));
            }
        }

        return true;
    }

    /// <summary>The text of a discriminator value for a message: a string as itself, anything else as JSON.</summary>
    public static string Describe(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? JsonText.GetString(value) : value.GetRawText();

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
