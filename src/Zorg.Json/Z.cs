using System;
using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using Zorg.Json.Import;
using Zorg.Json.Model;

namespace Zorg.Json;

/// <summary>
/// The untyped, Zod-style entry point. Add <c>using static Zorg.Json.Z;</c> to write <c>String()</c> instead of
/// <c>Z.String()</c>.
/// </summary>
public static class Z
{
    /// <summary>Creates a schema for JSON strings. JSON Schema: <c>type: string</c>. Zod equivalent: <c>z.string()</c>.</summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// StringSchema name = Z.String().MinLength(1).MaxLength(100);
    /// </code>
    /// </example>
    public static StringSchema String() => new(new SchemaNode { Types = JsonTypes.String });

    /// <summary>Creates a schema for JSON numbers, parsing to <see cref="double"/>. JSON Schema: <c>type: number</c>. Zod equivalent: <c>z.number()</c>.</summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// NumberSchema&lt;double&gt; price = Z.Number().Minimum(0);
    /// </code>
    /// </example>
    public static NumberSchema<double> Number() => Number<double>();

    /// <summary>Creates a schema for JSON integers, parsing to <see cref="long"/>. JSON Schema: <c>type: integer</c>. Zod equivalent: <c>z.int()</c>.</summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// NumberSchema&lt;long&gt; limit = Z.Integer().Minimum(1).Maximum(50);
    /// </code>
    /// </example>
    public static NumberSchema<long> Integer() => Number<long>();

    /// <summary>
    /// Creates a schema for numbers that fit <typeparamref name="TNumber"/>. JSON Schema: <c>type: integer</c> or
    /// <c>type: number</c>, with <c>format</c> for 32 and 64-bit types. Zod equivalent: <c>z.int32()</c> and similar.
    /// </summary>
    /// <typeparam name="TNumber">The numeric type.</typeparam>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// NumberSchema&lt;decimal&gt; price = Z.Number&lt;decimal&gt;().MultipleOf(0.01m);
    /// </code>
    /// </example>
    public static NumberSchema<TNumber> Number<TNumber>()
        where TNumber : struct, INumber<TNumber> => new(NumberSchema<TNumber>.CreateNode(strict: false));

    /// <summary>Creates a schema for JSON booleans. JSON Schema: <c>type: boolean</c>. Zod equivalent: <c>z.boolean()</c>.</summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// BooleanSchema active = Z.Boolean();
    /// </code>
    /// </example>
    public static BooleanSchema Boolean() => new(new SchemaNode { Types = JsonTypes.Boolean });

    /// <summary>Creates a schema that only accepts <c>null</c>. JSON Schema: <c>type: null</c>. Zod equivalent: <c>z.null()</c>.</summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// JsonValueSchema nothing = Z.Null();
    /// </code>
    /// </example>
    public static JsonValueSchema Null() => new(new SchemaNode { Types = JsonTypes.Null });

    /// <summary>Creates a schema that accepts anything. JSON Schema: <c>{}</c>. Zod equivalent: <c>z.unknown()</c>.</summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// JsonValueSchema anything = Z.Any();
    /// </code>
    /// </example>
    public static JsonValueSchema Any() => new(new SchemaNode());

    /// <summary>Creates a schema that accepts nothing. JSON Schema: <c>false</c>. Zod equivalent: <c>z.never()</c>.</summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// JsonValueSchema nothing = Z.Never();
    /// </code>
    /// </example>
    public static JsonValueSchema Never() => new(SchemaNode.False);

    /// <summary>Creates a schema for arrays of any values. JSON Schema: <c>type: array</c>. Zod equivalent: <c>z.array(z.unknown())</c>.</summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// ArraySchema&lt;JsonElement&gt; values = Z.Array();
    /// </code>
    /// </example>
    public static ArraySchema<JsonElement> Array() => new(new SchemaNode { Types = JsonTypes.Array }, static item => item.Clone(), static item => item);

    /// <summary>Creates a schema for arrays. JSON Schema: <c>type: array</c> and <c>items</c>. Zod equivalent: <c>z.array()</c>.</summary>
    /// <typeparam name="TItem">The type of each parsed item.</typeparam>
    /// <param name="items">The schema of each item.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// ArraySchema&lt;string&gt; tags = Z.Array(Z.String()).MaxItems(10);
    /// </code>
    /// </example>
    public static ArraySchema<TItem> Array<TItem>(Schema<TItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new(new SchemaNode { Types = JsonTypes.Array, Items = items.ToNode() }, items.Materialize, items.ToElement);
    }

    /// <summary>Creates a schema for fixed-length arrays. JSON Schema: <c>prefixItems</c> and <c>items: false</c>. Zod equivalent: <c>z.tuple()</c>.</summary>
    /// <param name="items">The schema of each position.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// JsonValueSchema point = Z.Tuple(Z.Number(), Z.Number());
    /// </code>
    /// </example>
    public static JsonValueSchema Tuple(params ReadOnlySpan<ISchema> items) => new(new SchemaNode
    {
        Types = JsonTypes.Array,
        PrefixItems = Nodes(items),
        Items = SchemaNode.False,
        MinItems = items.Length,
    });

    /// <summary>
    /// Creates a schema for JSON objects, which rejects unknown properties until told otherwise. JSON Schema:
    /// <c>type: object</c> and <c>additionalProperties: false</c>. Zod equivalent: <c>z.strictObject()</c>.
    /// </summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// ObjectSchema search = Z.Object()
    ///     .Property("query", Z.String().MinLength(1))
    ///     .Property("limit", Z.Integer().Default(10));
    /// </code>
    /// </example>
    public static ObjectSchema Object() => new(new SchemaNode
    {
        Types = JsonTypes.Object,
        Behavior = new SchemaBehavior { UnknownProperties = UnknownProperties.Reject },
    });

    /// <summary>Creates a schema for objects with any keys and uniform values. JSON Schema: <c>additionalProperties</c>. Zod equivalent: <c>z.record()</c>.</summary>
    /// <param name="values">The schema of each value.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// ObjectSchema scores = Z.Record(Z.Integer());
    /// </code>
    /// </example>
    public static ObjectSchema Record(ISchema values) => Object().AdditionalProperties(values);

    /// <summary>Creates a schema that matches at least one branch. JSON Schema: <c>anyOf</c>. Zod equivalent: <c>z.union()</c>.</summary>
    /// <param name="branches">The branches.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// JsonValueSchema id = Z.AnyOf(Z.String(), Z.Integer());
    /// </code>
    /// </example>
    public static JsonValueSchema AnyOf(params ReadOnlySpan<ISchema> branches) => new(new SchemaNode { AnyOf = Nodes(branches) });

    /// <summary>Creates a schema that matches exactly one branch. JSON Schema: <c>oneOf</c>. Zod equivalent: <c>z.xor()</c>.</summary>
    /// <param name="branches">The branches.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// JsonValueSchema id = Z.OneOf(Z.String(), Z.Integer());
    /// </code>
    /// </example>
    public static JsonValueSchema OneOf(params ReadOnlySpan<ISchema> branches) => new(new SchemaNode { OneOf = Nodes(branches) });

    /// <summary>Creates a schema that matches every branch. JSON Schema: <c>allOf</c>. Zod equivalent: <c>z.intersection()</c>.</summary>
    /// <param name="branches">
    /// The branches. Object branches must use <c>Passthrough</c>, because a closed object rejects the other branches' properties.
    /// </param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// JsonValueSchema shortName = Z.AllOf(Z.String().Pattern("^a"), Z.String().MaxLength(5));
    /// </code>
    /// </example>
    public static JsonValueSchema AllOf(params ReadOnlySpan<ISchema> branches) => new(new SchemaNode { AllOf = Nodes(branches) });

    /// <summary>Creates a schema that matches what another doesn't. JSON Schema: <c>not</c>.</summary>
    /// <param name="schema">The schema to exclude.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// JsonValueSchema notNull = Z.Not(Z.Null());
    /// </code>
    /// </example>
    public static JsonValueSchema Not(ISchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return new(new SchemaNode { Not = schema.ToNode() });
    }

    /// <summary>Creates a conditional schema. JSON Schema: <c>if</c>, <c>then</c> and <c>else</c>.</summary>
    /// <param name="condition">Selects the branch. Its issues are never reported.</param>
    /// <param name="then">Applies when the condition matches.</param>
    /// <param name="otherwise">Applies when it doesn't.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// JsonValueSchema schema = Z.If(Z.String(), Z.String().MinLength(1), Z.Integer());
    /// </code>
    /// </example>
    public static JsonValueSchema If(ISchema condition, ISchema? then = null, ISchema? otherwise = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return new(new SchemaNode { If = condition.ToNode(), Then = then?.ToNode(), Else = otherwise?.ToNode() });
    }

    /// <summary>
    /// Creates a union of objects selected by a property, with better errors and speed than <see cref="OneOf"/>.
    /// JSON Schema: <c>oneOf</c> with a <c>const</c> or <c>enum</c> in each branch. Zod equivalent: <c>z.discriminatedUnion()</c>.
    /// </summary>
    /// <param name="discriminator">The property that selects the branch.</param>
    /// <param name="branches">The branches, each declaring the property with a <c>Const</c> or <c>Enum</c>.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// JsonValueSchema shape = Z.DiscriminatedUnion("kind",
    ///     Z.Object().Property("kind", Z.String().Const("circle")).Property("radius", Z.Number()),
    ///     Z.Object().Property("kind", Z.String().Const("square")).Property("side", Z.Number()));
    /// </code>
    /// </example>
    public static JsonValueSchema DiscriminatedUnion(string discriminator, params ReadOnlySpan<ISchema> branches)
    {
        ArgumentNullException.ThrowIfNull(discriminator);
        return new(new SchemaNode { OneOf = Nodes(branches), Behavior = new SchemaBehavior { Discriminator = discriminator } });
    }

    /// <summary>
    /// Creates a reference to a schema that isn't built yet, for recursive schemas. JSON Schema: <c>$ref</c> into <c>$defs</c>.
    /// Zod equivalent: <c>z.lazy()</c>.
    /// </summary>
    /// <param name="name">The name under <c>$defs</c>.</param>
    /// <param name="schema">Gets the schema on first use.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// static readonly ObjectSchema Category = Z.Object()
    ///     .Property("name", Z.String())
    ///     .Property("children", Z.Array(Z.Lazy("category", () => Category)));
    /// </code>
    /// </example>
    public static JsonValueSchema Lazy(string name, Func<ISchema> schema)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(schema);
        Lazy<SchemaNode> target = new(() => schema().ToNode());

        return new(new SchemaNode
        {
            Ref = $"#/$defs/{name}",
            Behavior = new SchemaBehavior { DefinitionName = name, Target = () => target.Value },
        });
    }

    /// <summary>
    /// Imports a JSON Schema document (Draft 2020-12, draft-07 or draft-06), so that it validates like any other schema.
    /// JSON Schema: all of the core, applicator and validation vocabularies, except <c>$dynamicRef</c> and
    /// <c>unevaluated*</c> beside in-place applicators. Zod equivalent: <c>z.fromJSONSchema()</c>.
    /// </summary>
    /// <param name="schema">The document. It's copied, so it needn't outlive this call.</param>
    /// <param name="options">Dialect, format handling and limits. Null uses the defaults.</param>
    /// <returns>A new schema, with unknown properties allowed and no default values filled in.</returns>
    /// <exception cref="JsonSchemaImportException">The document can't be imported faithfully. Nothing is dropped silently.</exception>
    /// <example>
    /// <code>
    /// JsonValueSchema tool = Z.FromJsonSchema(JsonElement.Parse(inputSchema));
    /// ParseResult&lt;JsonElement&gt; result = tool.SafeParse(arguments);
    /// </code>
    /// </example>
    public static JsonValueSchema FromJsonSchema(JsonElement schema, JsonSchemaImportOptions? options = null) =>
        new(SchemaImporter.Import(schema, options));

    /// <summary>
    /// Imports a JSON Schema document from text. Malformed JSON and duplicate keys fail with
    /// <see cref="JsonSchemaImportCodes.InvalidSchema"/>. JSON Schema: see
    /// <see cref="FromJsonSchema(JsonElement, JsonSchemaImportOptions?)"/>. Zod equivalent: <c>z.fromJSONSchema()</c>.
    /// </summary>
    /// <param name="schema">The JSON text.</param>
    /// <param name="options">Dialect, format handling and limits. Null uses the defaults.</param>
    /// <returns>A new schema, with unknown properties allowed and no default values filled in.</returns>
    /// <exception cref="JsonSchemaImportException">The document can't be imported faithfully. Nothing is dropped silently.</exception>
    /// <example>
    /// <code>
    /// JsonValueSchema name = Z.FromJsonSchema("""{ "type": "string", "minLength": 1 }""");
    /// </code>
    /// </example>
    public static JsonValueSchema FromJsonSchema(string schema, JsonSchemaImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return new(SchemaImporter.Import(schema, options));
    }

    private static ImmutableArray<SchemaNode> Nodes(ReadOnlySpan<ISchema> schemas)
    {
        ImmutableArray<SchemaNode>.Builder nodes = ImmutableArray.CreateBuilder<SchemaNode>(schemas.Length);

        foreach (ISchema schema in schemas)
        {
            ArgumentNullException.ThrowIfNull(schema);
            nodes.Add(schema.ToNode());
        }

        return nodes.MoveToImmutable();
    }
}
