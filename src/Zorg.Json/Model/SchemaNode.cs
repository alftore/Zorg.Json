using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;

namespace Zorg.Json.Model;

/// <summary>
/// The immutable model behind every schema. Its properties are JSON Schema keywords, plus
/// <see cref="Behavior"/> for what has no keyword. Read it with <see cref="ISchema.ToNode"/>, and change
/// a copy with a <see langword="with"/> expression in an <see cref="Emission.ISchemaRewriter"/>.
/// </summary>
public sealed record SchemaNode
{
    internal SchemaNode()
    {
    }

    /// <summary>A node that accepts everything: the schema <c>true</c>.</summary>
    public static SchemaNode True { get; } = new() { BooleanValue = true };

    /// <summary>A node that rejects everything: the schema <c>false</c>.</summary>
    public static SchemaNode False { get; } = new() { BooleanValue = false };

    /// <summary>Set for the boolean schemas <c>true</c> and <c>false</c>.</summary>
    public bool? BooleanValue { get; init; }

    /// <summary>The <c>$id</c> keyword.</summary>
    public string? Id { get; init; }

    /// <summary>The <c>$anchor</c> keyword.</summary>
    public string? Anchor { get; init; }

    /// <summary>The <c>$ref</c> keyword.</summary>
    public string? Ref { get; init; }

    /// <summary>The <c>$dynamicRef</c> keyword. <see cref="SchemaBehavior.Target"/> is where it lands before the dynamic scope is read.</summary>
    public string? DynamicRef { get; init; }

    /// <summary>The <c>$dynamicAnchor</c> keyword.</summary>
    public string? DynamicAnchor { get; init; }

    /// <summary>The <c>$defs</c> keyword. Empty when absent.</summary>
    public ImmutableArray<SchemaProperty> Defs { get; init; } = [];

    /// <summary>The <c>title</c> keyword.</summary>
    public string? Title { get; init; }

    /// <summary>The <c>description</c> keyword.</summary>
    public string? Description { get; init; }

    /// <summary>The <c>$comment</c> keyword.</summary>
    public string? Comment { get; init; }

    /// <summary>The <c>default</c> keyword.</summary>
    public JsonElement? Default { get; init; }

    /// <summary>The <c>examples</c> keyword. Empty when absent.</summary>
    public ImmutableArray<JsonElement> Examples { get; init; } = [];

    /// <summary>The <c>deprecated</c> keyword.</summary>
    public bool? Deprecated { get; init; }

    /// <summary>The <c>readOnly</c> keyword.</summary>
    public bool? ReadOnly { get; init; }

    /// <summary>The <c>writeOnly</c> keyword.</summary>
    public bool? WriteOnly { get; init; }

    /// <summary>The <c>type</c> keyword.</summary>
    public JsonTypes Types { get; init; }

    /// <summary>The <c>const</c> keyword.</summary>
    public JsonElement? Const { get; init; }

    /// <summary>The <c>enum</c> keyword. Empty when absent.</summary>
    public ImmutableArray<JsonElement> Enum { get; init; } = [];

    /// <summary>The <c>minimum</c> keyword. A JSON number, so its literal text is kept.</summary>
    public JsonElement? Minimum { get; init; }

    /// <summary>The <c>maximum</c> keyword. A JSON number, so its literal text is kept.</summary>
    public JsonElement? Maximum { get; init; }

    /// <summary>The <c>exclusiveMinimum</c> keyword. A JSON number, so its literal text is kept.</summary>
    public JsonElement? ExclusiveMinimum { get; init; }

    /// <summary>The <c>exclusiveMaximum</c> keyword. A JSON number, so its literal text is kept.</summary>
    public JsonElement? ExclusiveMaximum { get; init; }

    /// <summary>The <c>multipleOf</c> keyword. A JSON number, so its literal text is kept.</summary>
    public JsonElement? MultipleOf { get; init; }

    /// <summary>The <c>minLength</c> keyword, in Unicode code points.</summary>
    public int? MinLength { get; init; }

    /// <summary>The <c>maxLength</c> keyword, in Unicode code points.</summary>
    public int? MaxLength { get; init; }

    /// <summary>The <c>pattern</c> keyword.</summary>
    public string? Pattern { get; init; }

    /// <summary>The <c>format</c> keyword.</summary>
    public string? Format { get; init; }

    /// <summary>The <c>contentEncoding</c> keyword, which is only an annotation.</summary>
    public string? ContentEncoding { get; init; }

    /// <summary>The <c>contentMediaType</c> keyword, which is only an annotation.</summary>
    public string? ContentMediaType { get; init; }

    /// <summary>The <c>contentSchema</c> keyword, which is only an annotation.</summary>
    public SchemaNode? ContentSchema { get; init; }

    /// <summary>The <c>prefixItems</c> keyword. Empty when absent.</summary>
    public ImmutableArray<SchemaNode> PrefixItems { get; init; } = [];

    /// <summary>The <c>items</c> keyword.</summary>
    public SchemaNode? Items { get; init; }

    /// <summary>The <c>contains</c> keyword.</summary>
    public SchemaNode? Contains { get; init; }

    /// <summary>The <c>minContains</c> keyword.</summary>
    public int? MinContains { get; init; }

    /// <summary>The <c>maxContains</c> keyword.</summary>
    public int? MaxContains { get; init; }

    /// <summary>The <c>minItems</c> keyword.</summary>
    public int? MinItems { get; init; }

    /// <summary>The <c>maxItems</c> keyword.</summary>
    public int? MaxItems { get; init; }

    /// <summary>The <c>uniqueItems</c> keyword.</summary>
    public bool? UniqueItems { get; init; }

    /// <summary>The <c>properties</c> keyword, in declaration order. Empty when absent.</summary>
    public ImmutableArray<SchemaProperty> Properties { get; init; } = [];

    /// <summary>The <c>required</c> keyword. Empty when absent.</summary>
    public ImmutableArray<string> Required { get; init; } = [];

    /// <summary>The <c>patternProperties</c> keyword. Empty when absent.</summary>
    public ImmutableArray<SchemaProperty> PatternProperties { get; init; } = [];

    /// <summary>The <c>additionalProperties</c> keyword.</summary>
    public SchemaNode? AdditionalProperties { get; init; }

    /// <summary>The <c>propertyNames</c> keyword.</summary>
    public SchemaNode? PropertyNames { get; init; }

    /// <summary>The <c>dependentRequired</c> keyword. Empty when absent.</summary>
    public ImmutableArray<KeyValuePair<string, ImmutableArray<string>>> DependentRequired { get; init; } = [];

    /// <summary>The <c>dependentSchemas</c> keyword. Empty when absent.</summary>
    public ImmutableArray<SchemaProperty> DependentSchemas { get; init; } = [];

    /// <summary>The <c>minProperties</c> keyword.</summary>
    public int? MinProperties { get; init; }

    /// <summary>The <c>maxProperties</c> keyword.</summary>
    public int? MaxProperties { get; init; }

    /// <summary>The <c>allOf</c> keyword. Empty when absent.</summary>
    public ImmutableArray<SchemaNode> AllOf { get; init; } = [];

    /// <summary>The <c>anyOf</c> keyword. Empty when absent.</summary>
    public ImmutableArray<SchemaNode> AnyOf { get; init; } = [];

    /// <summary>The <c>oneOf</c> keyword. Empty when absent.</summary>
    public ImmutableArray<SchemaNode> OneOf { get; init; } = [];

    /// <summary>The <c>not</c> keyword.</summary>
    public SchemaNode? Not { get; init; }

    /// <summary>The <c>if</c> keyword.</summary>
    public SchemaNode? If { get; init; }

    /// <summary>The <c>then</c> keyword.</summary>
    public SchemaNode? Then { get; init; }

    /// <summary>The <c>else</c> keyword.</summary>
    public SchemaNode? Else { get; init; }

    /// <summary>The <c>unevaluatedItems</c> keyword.</summary>
    public SchemaNode? UnevaluatedItems { get; init; }

    /// <summary>The <c>unevaluatedProperties</c> keyword.</summary>
    public SchemaNode? UnevaluatedProperties { get; init; }

    /// <summary>Unknown and <c>x-*</c> keywords, kept for emission. Empty when there are none.</summary>
    public ImmutableArray<KeyValuePair<string, JsonElement>> Extensions { get; init; } = [];

    /// <summary>Behaviour that has no JSON Schema keyword.</summary>
    public SchemaBehavior Behavior { get; init; } = SchemaBehavior.Empty;

    /// <summary>The direct subschemas of this node. A <c>$ref</c> target isn't a child.</summary>
    public IEnumerable<SchemaNode> Children
    {
        get
        {
            IEnumerable<SchemaNode> lists = Defs.Concat(Properties).Concat(PatternProperties).Concat(DependentSchemas).Select(property => property.Schema)
                .Concat(PrefixItems).Concat(AllOf).Concat(AnyOf).Concat(OneOf);

            SchemaNode?[] singles = [ContentSchema, Items, Contains, AdditionalProperties, PropertyNames, Not, If, Then, Else, UnevaluatedItems, UnevaluatedProperties];
            return lists.Concat(singles.OfType<SchemaNode>());
        }
    }

    /// <summary>Copies this node with <paramref name="map"/> applied to every direct subschema.</summary>
    /// <param name="map">The change to apply.</param>
    /// <returns>The new node.</returns>
    public SchemaNode MapChildren(Func<SchemaNode, SchemaNode> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return MapChildren((child, _) => map(child));
    }

    /// <summary>
    /// Maps every direct subschema and says where it sits. A subschema is in a negative position under <c>not</c>, in the
    /// condition of <c>if</c>, in one branch of several in <c>oneOf</c>, and in <c>contains</c> beside <c>maxContains</c>, or
    /// <c>unevaluated*</c>: a looser subschema there makes this node stricter.
    /// </summary>
    internal SchemaNode MapChildren(Func<SchemaNode, bool, SchemaNode> map)
    {
        SchemaNode? Single(SchemaNode? node, bool negative = false) => node is null ? null : map(node, negative);
        ImmutableArray<SchemaNode> List(ImmutableArray<SchemaNode> nodes, bool negative = false) =>
            nodes.IsEmpty ? nodes : [.. nodes.Select(node => map(node, negative))];
        ImmutableArray<SchemaProperty> Named(ImmutableArray<SchemaProperty> properties) =>
            properties.IsEmpty ? properties : [.. properties.Select(property => property with { Schema = map(property.Schema, false) })];

        return this with
        {
            Defs = Named(Defs),
            ContentSchema = Single(ContentSchema),
            PrefixItems = List(PrefixItems),
            Items = Single(Items),
            Contains = Single(Contains, negative: MaxContains is not null),
            Properties = Named(Properties),
            PatternProperties = Named(PatternProperties),
            AdditionalProperties = Single(AdditionalProperties),
            PropertyNames = Single(PropertyNames),
            DependentSchemas = Named(DependentSchemas),
            AllOf = List(AllOf),
            AnyOf = List(AnyOf),
            OneOf = List(OneOf, negative: OneOf.Length > 1),
            Not = Single(Not, negative: true),
            If = Single(If, negative: true),
            Then = Single(Then),
            Else = Single(Else),
            UnevaluatedItems = Single(UnevaluatedItems, negative: true),
            UnevaluatedProperties = Single(UnevaluatedProperties, negative: true),
        };
    }

    internal SchemaNode WithNull()
    {
        // A named node is one definition wherever it's used, so null goes beside it, not into it.
        if ((Types == JsonTypes.None && Const is null && Enum.IsEmpty) || Behavior.DefinitionName is not null)
        {
            // Presence belongs to the outermost node, because that's the one the parent object reads.
            return new SchemaNode
            {
                Default = Default,
                AnyOf = [this, new SchemaNode { Types = JsonTypes.Null }],
                Behavior = new SchemaBehavior { IsOptional = Behavior.IsOptional, FillDefault = Behavior.FillDefault },
            };
        }

        JsonElement nullValue = JsonElement.Parse("null");

        return this with
        {
            Types = Types == JsonTypes.None ? JsonTypes.None : Types | JsonTypes.Null,
            Const = null,
            Enum = Const is { } constant ? [constant, nullValue] : Enum.IsEmpty ? [] : Enum.Add(nullValue),
        };
    }
}

/// <summary>A named subschema, as found under <c>properties</c>, <c>$defs</c> and similar keywords.</summary>
/// <param name="Name">The name, or the pattern under <c>patternProperties</c>.</param>
/// <param name="Schema">The subschema.</param>
public sealed record SchemaProperty(string Name, SchemaNode Schema)
{
    /// <summary>Set for a property that's serialized but can't be read, such as a computed one.</summary>
    public bool OutputOnly { get; init; }
}
