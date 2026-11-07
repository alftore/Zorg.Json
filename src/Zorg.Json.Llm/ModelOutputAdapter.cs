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
internal sealed class ModelOutputAdapter(SchemaNode root, JsonSchemaProfile profile)
{
    private const string DefsPrefix = "#/$defs/";

    // Deeper than JsonNode.Parse reads. Anything below is left as it came.
    private const int MaxConvertDepth = 64;

    private const int MaxVisitDepth = 256;

    private const int MaxVisits = 1_000_000;

    private const int MaxFlattenDepth = 8;

    // Each trial copies and checks the value, and unions inside a branch try their own, so the total is capped.
    private const int MaxTrials = 256;

    private static readonly JsonDocumentOptions ResultOptions = new() { MaxDepth = int.MaxValue };

    private readonly Func<SchemaNode, SchemaNode> _rewrite = JsonSchemaEmitter.Rewriter(new JsonSchemaOptions { Target = profile });

    private readonly System.Collections.Generic.Dictionary<SchemaNode, SchemaNode> _emitted = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

    private readonly System.Collections.Generic.Dictionary<SchemaNode, ValidationPlan> _plans = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

    private int _visits;

    private int _trials;

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
            new ModelOutputAdapter(root, emitted.Target ?? JsonSchemaProfile.Draft202012).Visit(value, root, 0);
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

    private static bool Matches(JsonNode? value, SchemaNode node, SchemaNode emitted) => value switch
    {
        JsonObject target => MatchesObject(target, node, emitted),
        JsonArray => node.Types == JsonTypes.None || node.Types.HasFlag(JsonTypes.Array),
        _ => false,
    };

    private static bool MatchesObject(JsonObject value, SchemaNode node, SchemaNode emitted)
    {
        if (node.Types != JsonTypes.None && !node.Types.HasFlag(JsonTypes.Object))
        {
            return false;
        }

        if (node.Required.Any(name => !value.ContainsKey(name)))
        {
            return false;
        }

        // Where the emitted branch is closed, a model that keeps to the schema sends no property it doesn't name, by name or by pattern.
        if (emitted.Behavior.UnknownProperties == UnknownProperties.Reject
            && emitted.AdditionalProperties is null
            && value.Any(entry => emitted.Properties.All(property => property.Name != entry.Key) && !MatchesPattern(entry.Key, emitted)))
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

        if (!HasAllowedType(element, node.Types))
        {
            return false;
        }

        if (node.Const is { } constant && !JsonEquality.AreEqual(constant, element))
        {
            return element.ValueKind == JsonValueKind.Null;
        }

        return node.Enum.IsEmpty || element.ValueKind == JsonValueKind.Null || node.Enum.Any(item => JsonEquality.AreEqual(item, element));
    }

    // Null passes, because the profile made optional properties nullable. Integer is read as number, which only lets more through.
    private static bool HasAllowedType(JsonElement element, JsonTypes types) => types == JsonTypes.None || element.ValueKind switch
    {
        JsonValueKind.String => types.HasFlag(JsonTypes.String),
        JsonValueKind.True or JsonValueKind.False => types.HasFlag(JsonTypes.Boolean),
        JsonValueKind.Number => types.HasFlag(JsonTypes.Number) || types.HasFlag(JsonTypes.Integer),
        JsonValueKind.Object => types.HasFlag(JsonTypes.Object),
        JsonValueKind.Array => types.HasFlag(JsonTypes.Array),
        _ => true,
    };

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

        ImmutableArray<SchemaNode> branches = node.AnyOf.IsEmpty ? node.OneOf : node.AnyOf;

        if (!branches.IsEmpty && !TryEachBranch(value, branches, depth) && Select(value, branches) is { } selected)
        {
            Visit(value, selected, depth + 1);
        }
    }

    /// <summary>
    /// Takes the first branch whose emitted form accepts the answer, which is one the model kept to, and whose own schema
    /// accepts the answer once adapted. When none does, the caller guesses from the shape, so that parsing reports on the
    /// likeliest branch.
    /// </summary>
    private bool TryEachBranch(JsonNode value, ImmutableArray<SchemaNode> branches, int depth)
    {
        foreach (SchemaNode branch in branches)
        {
            if (Resolve(branch) is not { } resolved)
            {
                continue;
            }

            if (++_trials > MaxTrials)
            {
                return false;
            }

            if (!Accepts(Emitted(resolved), value))
            {
                continue;
            }

            JsonNode copy = value.DeepClone();
            Visit(copy, resolved, depth + 1);

            if (Accepts(resolved, copy))
            {
                MoveInto(value, copy);
                return true;
            }
        }

        return false;
    }

    private bool Accepts(SchemaNode node, JsonNode value)
    {
        if (!_plans.TryGetValue(node, out ValidationPlan? plan))
        {
            plan = ValidationPlan.Create(node);
            _plans[node] = plan;
        }

        JsonElement element = ToElement(value);
        ValidationContext context = new(element);
        plan.Run(element, context);

        // Async refinements wait for the parse, which runs them on what's chosen here.
        return !context.HasIssues && !context.Halted;
    }

    private static void MoveInto(JsonNode target, JsonNode source)
    {
        if (target is JsonObject members && source is JsonObject adapted)
        {
            members.Clear();

            foreach ((string name, JsonNode? item) in adapted.ToArray())
            {
                adapted.Remove(name);
                members[name] = item;
            }
        }
        else if (target is JsonArray items && source is JsonArray adaptedItems)
        {
            JsonNode?[] moved = [.. adaptedItems];
            adaptedItems.Clear();
            items.Clear();

            foreach (JsonNode? item in moved)
            {
                items.Add(item);
            }
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
        branches.FirstOrDefault(branch => Resolve(branch) is { } resolved && Matches(value, Flattened(resolved, 0), Emitted(resolved)));

    /// <summary>The branch as the profile emitted it, which is what the model kept to.</summary>
    private SchemaNode Emitted(SchemaNode node)
    {
        if (!_emitted.TryGetValue(node, out SchemaNode? emitted))
        {
            emitted = _rewrite(node);
            _emitted[node] = emitted;
        }

        return emitted;
    }

    /// <summary>An <c>allOf</c> says what its branches say together, as the object the profile made of it does.</summary>
    private SchemaNode Flattened(SchemaNode node, int depth)
    {
        if (node.AllOf.IsEmpty || depth >= MaxFlattenDepth)
        {
            return node;
        }

        SchemaNode[] branches = [.. node.AllOf.Select(Resolve).OfType<SchemaNode>().Select(branch => Flattened(branch, depth + 1))];

        return node with
        {
            Types = node.Types != JsonTypes.None ? node.Types : branches.FirstOrDefault(branch => branch.Types != JsonTypes.None)?.Types ?? JsonTypes.None,
            Properties = [.. node.Properties.Concat(branches.SelectMany(branch => branch.Properties)).DistinctBy(property => property.Name)],
            Required = [.. node.Required.Concat(branches.SelectMany(branch => branch.Required)).Distinct(StringComparer.Ordinal)],
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
