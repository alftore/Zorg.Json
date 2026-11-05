using System;
using System.Collections.Immutable;
using System.Diagnostics.Contracts;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Zorg.Json.Model;

namespace Zorg.Json;

/// <summary>
/// A schema for JSON numbers, parsing to any .NET numeric type. Create one with <see cref="Z.Number()"/>,
/// <see cref="Z.Integer"/> or <see cref="Z.Number{TNumber}"/>. Zod equivalent: <c>ZodNumber</c>.
/// </summary>
/// <typeparam name="TNumber">The numeric type, such as <see cref="int"/> or <see cref="decimal"/>.</typeparam>
public sealed class NumberSchema<TNumber> : Schema<TNumber, NumberSchema<TNumber>>
    where TNumber : struct, INumber<TNumber>
{
    internal NumberSchema(SchemaNode node)
        : base(node)
    {
    }

    internal static bool IsIntegerType => TNumber.One / (TNumber.One + TNumber.One) == TNumber.Zero;

    /// <summary>Requires at least this value. JSON Schema: <c>minimum</c>. Zod equivalent: <c>.gte()</c>.</summary>
    /// <param name="value">The inclusive lower limit.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// NumberSchema&lt;long&gt; schema = Z.Integer().Minimum(0);
    /// </code>
    /// </example>
    [Pure]
    public NumberSchema<TNumber> Minimum(TNumber value) => With(Node with { Minimum = ToElement(value) });

    /// <summary>Requires at most this value. JSON Schema: <c>maximum</c>. Zod equivalent: <c>.lte()</c>.</summary>
    /// <param name="value">The inclusive upper limit.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// NumberSchema&lt;long&gt; schema = Z.Integer().Maximum(150);
    /// </code>
    /// </example>
    [Pure]
    public NumberSchema<TNumber> Maximum(TNumber value) => With(Node with { Maximum = ToElement(value) });

    /// <summary>Requires more than this value. JSON Schema: <c>exclusiveMinimum</c>. Zod equivalent: <c>.gt()</c>.</summary>
    /// <param name="value">The exclusive lower limit.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// NumberSchema&lt;double&gt; schema = Z.Number().ExclusiveMinimum(0);
    /// </code>
    /// </example>
    [Pure]
    public NumberSchema<TNumber> ExclusiveMinimum(TNumber value) => With(Node with { ExclusiveMinimum = ToElement(value) });

    /// <summary>Requires less than this value. JSON Schema: <c>exclusiveMaximum</c>. Zod equivalent: <c>.lt()</c>.</summary>
    /// <param name="value">The exclusive upper limit.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// NumberSchema&lt;double&gt; schema = Z.Number().ExclusiveMaximum(1);
    /// </code>
    /// </example>
    [Pure]
    public NumberSchema<TNumber> ExclusiveMaximum(TNumber value) => With(Node with { ExclusiveMaximum = ToElement(value) });

    /// <summary>Requires a multiple of this value, computed exactly. JSON Schema: <c>multipleOf</c>. Zod equivalent: <c>.multipleOf()</c>.</summary>
    /// <param name="value">The divisor, which must be positive.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// NumberSchema&lt;decimal&gt; schema = Z.Number&lt;decimal&gt;().MultipleOf(0.01m);
    /// </code>
    /// </example>
    [Pure]
    public NumberSchema<TNumber> MultipleOf(TNumber value)
    {
        if (value <= TNumber.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "The divisor must be positive.");
        }

        return With(Node with { MultipleOf = ToElement(value) });
    }

    /// <summary>Requires more than zero. JSON Schema: <c>exclusiveMinimum: 0</c>. Zod equivalent: <c>.positive()</c>.</summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// NumberSchema&lt;double&gt; schema = Z.Number().Positive();
    /// </code>
    /// </example>
    [Pure]
    public NumberSchema<TNumber> Positive() => ExclusiveMinimum(TNumber.Zero);

    /// <summary>Requires less than zero. JSON Schema: <c>exclusiveMaximum: 0</c>. Zod equivalent: <c>.negative()</c>.</summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// NumberSchema&lt;double&gt; schema = Z.Number().Negative();
    /// </code>
    /// </example>
    [Pure]
    public NumberSchema<TNumber> Negative() => ExclusiveMaximum(TNumber.Zero);

    /// <summary>Requires exactly this value. JSON Schema: <c>const</c>. Zod equivalent: <c>z.literal()</c>.</summary>
    /// <param name="value">The only allowed value.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// NumberSchema&lt;long&gt; schema = Z.Integer().Const(1);
    /// </code>
    /// </example>
    [Pure]
    public NumberSchema<TNumber> Const(TNumber value)
    {
        SchemaNode node = Node with { Const = ToElement(value) };
        return With(Node.Types.HasFlag(JsonTypes.Null) ? node.WithNull() : node);
    }

    /// <summary>Requires one of these values. JSON Schema: <c>enum</c>. Zod equivalent: <c>z.literal([...])</c>.</summary>
    /// <param name="values">The allowed values.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// NumberSchema&lt;long&gt; schema = Z.Integer().Enum(1, 2, 3);
    /// </code>
    /// </example>
    [Pure]
    public NumberSchema<TNumber> Enum(params ReadOnlySpan<TNumber> values)
    {
        ImmutableArray<JsonElement>.Builder elements = ImmutableArray.CreateBuilder<JsonElement>(values.Length);

        foreach (TNumber value in values)
        {
            elements.Add(ToElement(value));
        }

        SchemaNode node = Node with { Enum = elements.MoveToImmutable() };
        return With(Node.Types.HasFlag(JsonTypes.Null) ? node.WithNull() : node);
    }

    /// <summary>
    /// Also accepts a string holding a JSON number, like System.Text.Json's <c>AllowReadingFromString</c>. The input
    /// schema adds <c>string</c> to <c>type</c>, with a <c>pattern</c>. Zod equivalent: <c>z.coerce.number()</c>.
    /// </summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// NumberSchema&lt;long&gt; schema = Z.Integer().Coerce();
    /// </code>
    /// </example>
    [Pure]
    public NumberSchema<TNumber> Coerce() => With(Node with { Behavior = Node.Behavior with { Coercion = SchemaCoercion.Number } });

    /// <summary>
    /// Also allows JSON <c>null</c>. JSON Schema: adds <c>null</c> to <c>type</c>. Zod equivalent: <c>.nullable()</c>.
    /// Call it last, because the result has no number methods.
    /// </summary>
    /// <returns>A new schema that parses to a nullable number.</returns>
    /// <example>
    /// <code>
    /// Schema&lt;int?&gt; schema = Z.Number&lt;int&gt;().Minimum(0).Nullable();
    /// </code>
    /// </example>
    [Pure]
    public NullableSchema<TNumber> Nullable() => new(Node.WithNull(), this);

    private protected override NumberSchema<TNumber> With(SchemaNode node) => new(node);

    internal static SchemaNode CreateNode(bool strict)
    {
        NumberStyles styles = strict && IsIntegerType ? NumberStyles.AllowLeadingSign : NumberStyles.Float;

        return new SchemaNode
        {
            Types = IsIntegerType ? JsonTypes.Integer : JsonTypes.Number,
            Behavior = new SchemaBehavior
            {
                NumberCheck = text => TNumber.TryParse(text, styles, CultureInfo.InvariantCulture, out TNumber number) && TNumber.IsFinite(number) && !Underflows(text, number),
                NumberType = typeof(TNumber) == typeof(int) ? "int32"
                    : typeof(TNumber) == typeof(long) ? "int64"
                    : typeof(TNumber) == typeof(float) ? "float"
                    : typeof(TNumber) == typeof(double) ? "double"
                    : string.Empty,
            },
        };
    }

    /// <summary>A number that isn't zero but reads as zero has lost its value, so it doesn't fit the type, as an overflow doesn't.</summary>
    private static bool Underflows(string text, TNumber number) =>
        TNumber.IsZero(number) && !Validation.ExactNumber.Parse(text).Mantissa.IsZero;

    internal override TNumber Materialize(JsonElement value) =>
        TNumber.Parse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture);

    internal override JsonElement ToElement(TNumber value)
    {
        if (!TNumber.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "JSON has no NaN or Infinity.");
        }

        return JsonElement.Parse(value.ToString(null, CultureInfo.InvariantCulture));
    }
}

/// <summary>A schema for a JSON number or <c>null</c>. Create one with <c>Nullable()</c> on a <see cref="NumberSchema{TNumber}"/>.</summary>
/// <typeparam name="TNumber">The numeric type.</typeparam>
public sealed class NullableSchema<TNumber> : Schema<TNumber?, NullableSchema<TNumber>>
    where TNumber : struct, INumber<TNumber>
{
    private readonly NumberSchema<TNumber> _inner;

    internal NullableSchema(SchemaNode node, NumberSchema<TNumber> inner)
        : base(node) => _inner = inner;

    private protected override NullableSchema<TNumber> With(SchemaNode node) => new(node, _inner);

    internal override TNumber? Materialize(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : _inner.Materialize(value);

    internal override JsonElement ToElement(TNumber? value) =>
        value is { } number ? _inner.ToElement(number) : JsonElement.Parse("null");
}
