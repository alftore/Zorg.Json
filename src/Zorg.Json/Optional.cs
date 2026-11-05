using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Zorg.Json;

/// <summary>What the omit-unset modifier needs to know about an <see cref="Optional{T}"/> without naming its type argument.</summary>
internal interface IOptional
{
    public bool IsSet { get; }
}

/// <summary>
/// A value that is either absent or set, for the body of a PATCH request. It tells a property that was left out
/// from one that was sent as <c>null</c>, which a plain <c>string?</c> can't.
/// </summary>
/// <typeparam name="T">The type of the value. Use <c>Optional&lt;string?&gt;</c> to let the property be set to <c>null</c>.</typeparam>
/// <remarks>
/// <para>
/// <b>Reading.</b> A property that is missing from the JSON is left as <c>default</c>, which is unset. A JSON
/// <c>null</c> reaches <see cref="OptionalJsonConverter{T}"/>, so it becomes a set value of <see langword="null"/>. Put
/// <c>[JsonConverter(typeof(OptionalJsonConverter&lt;string&gt;))]</c> on each property, which works with source generation and
/// Native AOT. <see cref="OptionalJsonConverterFactory"/> serves reflection-based code.
/// </para>
/// <para>
/// <b>Writing.</b> System.Text.Json writes a property name before it asks the converter for the value, so a converter can't
/// leave an unset value out; it writes <c>null</c>. Add <c>[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]</c>
/// to the property (an unset value is <c>default</c>, a set one never is), or call <see cref="OptionalJson.OmitUnset"/>
/// as a type info modifier.
/// </para>
/// <para>
/// <b>Schemas.</b> A typed schema treats an <c>Optional&lt;T&gt;</c> property as not required, with the schema of
/// <c>T</c>. JSON <c>null</c> is accepted when <c>T</c> is nullable. There is no JSON Schema keyword for it beyond <c>required</c>.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public sealed class PersonPatch
/// {
///     [JsonConverter(typeof(OptionalJsonConverter&lt;string&gt;))]
///     [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
///     public Optional&lt;string&gt; Name { get; init; }
/// }
///
/// if (patch.Name.IsSet)
/// {
///     person.Name = patch.Name.Value;
/// }
/// </code>
/// </example>
public readonly struct Optional<T> : IEquatable<Optional<T>>, IOptional
{
    private readonly T? _value;

    /// <summary>Creates a set value.</summary>
    /// <param name="value">The value, which may be <see langword="null"/> when <typeparamref name="T"/> allows it.</param>
    /// <example>
    /// <code>
    /// Optional&lt;string?&gt; cleared = new(null);
    /// </code>
    /// </example>
    public Optional(T? value)
    {
        _value = value;
        IsSet = true;
    }

    /// <summary>The unset value, the same as <c>default</c>.</summary>
    /// <example>
    /// <code>
    /// Optional&lt;int&gt; absent = Optional&lt;int&gt;.Unset;
    /// </code>
    /// </example>
    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Optional<T>.Unset reads better than a helper class, and default is the same value.")]
    public static Optional<T> Unset => default;

    /// <summary>Whether the value was present. False for an absent property.</summary>
    /// <example>
    /// <code>
    /// bool present = patch.Name.IsSet;
    /// </code>
    /// </example>
    public bool IsSet { get; }

    /// <summary>The value.</summary>
    /// <exception cref="InvalidOperationException">The value is unset.</exception>
    /// <example>
    /// <code>
    /// string name = patch.Name.Value;
    /// </code>
    /// </example>
    public T Value => IsSet ? _value! : throw new InvalidOperationException("The value is not set.");

    /// <summary>Gets the value, or the default of <typeparamref name="T"/> when it's unset.</summary>
    /// <returns>The value or <see langword="default"/>.</returns>
    /// <example>
    /// <code>
    /// string? name = patch.Name.GetValueOrDefault();
    /// </code>
    /// </example>
    public T? GetValueOrDefault() => _value;

    /// <summary>Gets the value, or a fallback when it's unset.</summary>
    /// <param name="fallback">The result for an unset value.</param>
    /// <returns>The value or <paramref name="fallback"/>.</returns>
    /// <example>
    /// <code>
    /// string name = patch.Name.GetValueOrDefault(person.Name);
    /// </code>
    /// </example>
    public T GetValueOrDefault(T fallback) => IsSet ? _value! : fallback;

    /// <summary>Gets the value if it's set.</summary>
    /// <param name="value">The value, when set.</param>
    /// <returns>Whether it's set.</returns>
    /// <example>
    /// <code>
    /// if (patch.Name.TryGetValue(out string? name))
    /// {
    ///     person.Name = name;
    /// }
    /// </code>
    /// </example>
    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        value = _value;
        return IsSet;
    }

    /// <summary>Converts a value to a set one.</summary>
    /// <param name="value">The value.</param>
    /// <returns>A set value.</returns>
    /// <example>
    /// <code>
    /// Optional&lt;string&gt; name = "Ada";
    /// </code>
    /// </example>
    [SuppressMessage("Usage", "CA2225:Operator overloads have named alternates", Justification = "The constructor is the named alternate.")]
    public static implicit operator Optional<T>(T value) => new(value);

    /// <summary>Compares two values: unset equals unset, and set values compare by their content.</summary>
    /// <param name="other">The other value.</param>
    /// <returns>Whether they are equal.</returns>
    /// <example>
    /// <code>
    /// bool same = new Optional&lt;int&gt;(1).Equals(new Optional&lt;int&gt;(1));
    /// </code>
    /// </example>
    public bool Equals(Optional<T> other) => IsSet == other.IsSet && EqualityComparer<T?>.Default.Equals(_value, other._value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Optional<T> other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => IsSet ? HashCode.Combine(true, _value) : 0;

    /// <inheritdoc/>
    public override string ToString() => IsSet ? _value?.ToString() ?? "null" : "unset";

    /// <summary>Compares two values.</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns>Whether they are equal.</returns>
    /// <example>
    /// <code>
    /// bool same = patch.Name == Optional&lt;string&gt;.Unset;
    /// </code>
    /// </example>
    public static bool operator ==(Optional<T> left, Optional<T> right) => left.Equals(right);

    /// <summary>Compares two values.</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns>Whether they differ.</returns>
    /// <example>
    /// <code>
    /// bool changed = patch.Name != Optional&lt;string&gt;.Unset;
    /// </code>
    /// </example>
    public static bool operator !=(Optional<T> left, Optional<T> right) => !left.Equals(right);
}
