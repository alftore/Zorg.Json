using System;
using System.Buffers;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Zorg.Json.Emission;
using Zorg.Json.Model;
using Zorg.Json.Validation;

namespace Zorg.Json.Llm;

/// <summary>Turns JSON shaped by an emitted schema back into the shape the full schema expects.</summary>
internal sealed class ModelOutputAdapter(SchemaNode root)
{
    private const string DefsPrefix = "#/$defs/";

    // Deeper than JsonNode.Parse reads. Anything below is left as it came.
    private const int MaxConvertDepth = 64;

    private const int MaxVisitDepth = 256;

    private const int MaxVisits = 1_000_000;

    private const int MaxFlattenDepth = 8;

    private static readonly JsonDocumentOptions ResultOptions = new() { MaxDepth = int.MaxValue };

    private int _visits;

    public static JsonElement Adapt(SchemaNode root, EmitResult emitted, JsonElement json)
    {
        // There is no value to convert. SafeParse reports it.
        if (json.ValueKind == JsonValueKind.Undefined)
        {
            return json;
        }

        ImmutableArray<ISchemaRewriter> rewriters = emitted.Target?.Rewriters ?? [];
        bool requiredAll = rewriters.Contains(Rewriters.RequireAllAsNullable);
        bool unwrap = rewriters.Contains(Rewriters.RequireObjectRoot) && !Rewriters.HasObjectRoot(root) && IsWrapper(emitted.Schema);

        if (!unwrap && !requiredAll)
        {
            return json;
        }

        JsonNode? value;

        try
        {
            value = ToNode(json, 0);
        }
        catch (InvalidOperationException)
        {
            // A property name that can't be decoded. SafeParse reports on the element as it is.
            return json;
        }

        if (unwrap && value is JsonObject wrapper && wrapper.Count == 1 && wrapper.TryGetPropertyValue(Rewriters.RootWrapperProperty, out JsonNode? inner))
        {
            wrapper.Remove(Rewriters.RootWrapperProperty);
            value = inner;
        }

        if (requiredAll)
        {
            new ModelOutputAdapter(root).Visit(value, root, 0);
        }

        return ToElement(value);
    }

    private static JsonNode? ToNode(JsonElement element, int depth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object when depth < MaxConvertDepth:
                JsonObject target = [];

                foreach (JsonProperty property in element.EnumerateObject())
                {
                    target[property.Name] = ToNode(property.Value, depth + 1);
                }

                return target;
            case JsonValueKind.Array when depth < MaxConvertDepth:
                JsonArray array = [];

                foreach (JsonElement item in element.EnumerateArray())
                {
                    array.Add(ToNode(item, depth + 1));
                }

                return array;
            default:
                return JsonValue.Create(element);
        }
    }

    // Leaves are written as the text they came in, so what System.Text.Json can't decode, such as a lone surrogate or a huge exponent, passes through.
    private static JsonElement ToElement(JsonNode? node)
    {
        ArrayBufferWriter<byte> buffer = new();

        using (Utf8JsonWriter writer = new(buffer))
        {
            Write(writer, node);
        }

        return JsonElement.Parse(buffer.WrittenSpan, ResultOptions);
    }

    private static void Write(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject target:
                writer.WriteStartObject();

                foreach ((string name, JsonNode? item) in target)
                {
                    writer.WritePropertyName(name);
                    Write(writer, item);
                }

                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();

                foreach (JsonNode? item in array)
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValue leaf when leaf.TryGetValue(out JsonElement element):
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;
            default:
                node.WriteTo(writer);
                break;
        }
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

        // The profiles close what strips, so a model that keeps to the schema sends no property such a branch doesn't name, by name or by pattern.
        if (node.Behavior.UnknownProperties is UnknownProperties.Reject or UnknownProperties.Strip
            && node.AdditionalProperties is null
            && value.Any(entry => node.Properties.All(property => property.Name != entry.Key) && !MatchesPattern(entry.Key, node)))
        {
            return false;
        }

        return node.Properties.All(property => !value.TryGetPropertyValue(property.Name, out JsonNode? item) || HasAllowedValue(item, property.Schema));
    }

    private static bool MatchesPattern(string name, SchemaNode node)
    {
        foreach (SchemaProperty pattern in node.PatternProperties)
        {
            try
            {
                if (PatternCompiler.Compile(pattern.Name).IsMatch(name))
                {
                    return true;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // It can't be told here, and parsing reports the timeout.
                return true;
            }
        }

        return false;
    }

    private static bool HasAllowedValue(JsonNode? value, SchemaNode node)
    {
        JsonElement element = ToElement(value);

        if (node.Const is { } constant && !JsonEquality.AreEqual(constant, element))
        {
            return element.ValueKind == JsonValueKind.Null;
        }

        return node.Enum.IsEmpty || element.ValueKind == JsonValueKind.Null || node.Enum.Any(item => JsonEquality.AreEqual(item, element));
    }

    private void Visit(JsonNode? value, SchemaNode schema, int depth)
    {
        if (depth > MaxVisitDepth || ++_visits > MaxVisits)
        {
            return;
        }

        SchemaNode? node = Resolve(schema);

        if (node is null)
        {
            return;
        }

        if (value is JsonObject target)
        {
            VisitProperties(target, node, depth);
        }
        else if (value is JsonArray array)
        {
            VisitItems(array, node, depth);
        }
        else
        {
            return;
        }

        foreach (SchemaNode branch in node.AllOf)
        {
            Visit(value, branch, depth + 1);
        }

        if (Select(value, node.AnyOf.IsEmpty ? node.OneOf : node.AnyOf) is { } selected)
        {
            Visit(value, selected, depth + 1);
        }
    }

    private void VisitProperties(JsonObject value, SchemaNode node, int depth)
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
                Visit(item, property.Schema, depth + 1);
            }
        }

        if (node.AdditionalProperties is { } additional)
        {
            foreach ((string name, JsonNode? item) in value.ToArray())
            {
                if (node.Properties.All(property => property.Name != name))
                {
                    Visit(item, additional, depth + 1);
                }
            }
        }
    }

    private void VisitItems(JsonArray value, SchemaNode node, int depth)
    {
        for (int index = 0; index < value.Count; index++)
        {
            SchemaNode? item = index < node.PrefixItems.Length ? node.PrefixItems[index] : node.Items;

            if (item is not null)
            {
                Visit(value[index], item, depth + 1);
            }
        }
    }

    private SchemaNode? Select(JsonNode? value, ImmutableArray<SchemaNode> branches) =>
        branches.FirstOrDefault(branch => Resolve(branch) is { } resolved && Matches(value, Flattened(resolved, 0)));

    /// <summary>An <c>allOf</c> says what its branches say together, as the object the profile made of it does.</summary>
    private SchemaNode Flattened(SchemaNode node, int depth)
    {
        if (node.AllOf.IsEmpty || depth >= MaxFlattenDepth)
        {
            return node;
        }

        SchemaNode[] branches = [.. node.AllOf.Select(Resolve).OfType<SchemaNode>().Select(branch => Flattened(branch, depth + 1))];
        bool closes = branches.Length == node.AllOf.Length && branches.All(branch => branch.Behavior.UnknownProperties != UnknownProperties.Allow && branch.PatternProperties.IsEmpty);

        return node with
        {
            Types = node.Types != JsonTypes.None ? node.Types : branches.FirstOrDefault(branch => branch.Types != JsonTypes.None)?.Types ?? JsonTypes.None,
            Properties = [.. node.Properties.Concat(branches.SelectMany(branch => branch.Properties)).DistinctBy(property => property.Name)],
            Required = [.. node.Required.Concat(branches.SelectMany(branch => branch.Required)).Distinct(StringComparer.Ordinal)],
            Behavior = node.Behavior with { UnknownProperties = closes ? UnknownProperties.Strip : UnknownProperties.Allow },
        };
    }

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
