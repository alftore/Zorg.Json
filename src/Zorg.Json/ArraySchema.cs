using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text.Json;
using Zorg.Json.Model;
using Zorg.Json.Validation;

namespace Zorg.Json;

/// <summary>A schema for JSON arrays. Create one with <see cref="Z.Array{TItem}"/>. Zod equivalent: <c>ZodArray</c>.</summary>
/// <typeparam name="TItem">The type of each parsed item.</typeparam>
public sealed class ArraySchema<TItem> : Schema<IReadOnlyList<TItem>, ArraySchema<TItem>>
{
    private readonly Func<JsonElement, TItem> _materializeItem;
    private readonly Func<TItem, JsonElement> _itemToElement;

    internal ArraySchema(SchemaNode node, Func<JsonElement, TItem> materializeItem, Func<TItem, JsonElement> itemToElement)
        : base(node)
    {
        _materializeItem = materializeItem;
        _itemToElement = itemToElement;
    }

    /// <summary>Requires at least this many items. JSON Schema: <c>minItems</c>. Zod equivalent: <c>.min()</c>.</summary>
    /// <param name="count">The minimum count.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// ArraySchema&lt;string&gt; schema = Z.Array(Z.String()).MinItems(1);
    /// </code>
    /// </example>
    [Pure]
    public ArraySchema<TItem> MinItems(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return With(Node with { MinItems = count });
    }

    /// <summary>Allows at most this many items. JSON Schema: <c>maxItems</c>. Zod equivalent: <c>.max()</c>.</summary>
    /// <param name="count">The maximum count.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// ArraySchema&lt;string&gt; schema = Z.Array(Z.String()).MaxItems(10);
    /// </code>
    /// </example>
    [Pure]
    public ArraySchema<TItem> MaxItems(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return With(Node with { MaxItems = count });
    }

    /// <summary>Requires at least one item. JSON Schema: <c>minItems: 1</c>. Zod equivalent: <c>.nonempty()</c>.</summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// ArraySchema&lt;string&gt; schema = Z.Array(Z.String()).NonEmpty();
    /// </code>
    /// </example>
    [Pure]
    public ArraySchema<TItem> NonEmpty() => MinItems(1);

    /// <summary>Requires all items to differ. JSON Schema: <c>uniqueItems</c>.</summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// ArraySchema&lt;string&gt; schema = Z.Array(Z.String()).UniqueItems();
    /// </code>
    /// </example>
    [Pure]
    public ArraySchema<TItem> UniqueItems() => With(Node with { UniqueItems = true });

    /// <summary>Sets the schema of every item. JSON Schema: <c>items</c>. Zod equivalent: the argument of <c>z.array()</c>.</summary>
    /// <param name="schema">The item schema.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// .Property(p => p.Addresses, s => s.MinItems(1).Items(AddressSchema))
    /// </code>
    /// </example>
    [Pure]
    public ArraySchema<TItem> Items(Schema<TItem> schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return new(Node with { Items = schema.ToNode() }, schema.Materialize, schema.ToElement);
    }

    /// <summary>
    /// Requires an item that matches. JSON Schema: <c>contains</c>, with <c>minContains</c> and <c>maxContains</c>
    /// when given.
    /// </summary>
    /// <param name="schema">The schema to match.</param>
    /// <param name="minimum">How many items must match, not negative. Default: 1.</param>
    /// <param name="maximum">How many items may match, not negative. Default: no limit.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// ArraySchema&lt;string&gt; schema = Z.Array(Z.String()).Contains(Z.String().Const("admin"));
    /// </code>
    /// </example>
    [Pure]
    public ArraySchema<TItem> Contains(ISchema schema, int? minimum = null, int? maximum = null)
    {
        ArgumentNullException.ThrowIfNull(schema);

        if (minimum is { } least)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(least, nameof(minimum));
        }

        if (maximum is { } most)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(most, nameof(maximum));
        }

        return With(Node with { Contains = schema.ToNode(), MinContains = minimum, MaxContains = maximum });
    }

    /// <summary>Also allows JSON <c>null</c>. JSON Schema: adds <c>null</c> to <c>type</c>. Zod equivalent: <c>.nullable()</c>.</summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// ArraySchema&lt;string&gt; schema = Z.Array(Z.String()).Nullable();
    /// </code>
    /// </example>
    [Pure]
    public ArraySchema<TItem> Nullable() => With(Node.WithNull());

    private protected override ArraySchema<TItem> With(SchemaNode node) => new(node, _materializeItem, _itemToElement);

    internal override IReadOnlyList<TItem> Materialize(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null! : [.. value.EnumerateArray().Select(_materializeItem)];

    internal override JsonElement ToElement(IReadOnlyList<TItem> value) => JsonStrings.ToArray(value.Select(_itemToElement));
}
