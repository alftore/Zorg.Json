using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Zorg.Json.Model;

namespace Zorg.Json;

/// <summary>
/// A schema bound to a .NET type. Every property of the type's System.Text.Json contract is already included, so
/// <see cref="Property(Expression{Func{T, string}}, Func{StringSchema, StringSchema})"/> only adds rules. Create one with
/// <see cref="Schema.Object{T}(JsonTypeInfo{T})"/>. Zod equivalent: <c>z.object()</c> with <c>z.infer</c>, turned around.
/// </summary>
/// <typeparam name="T">The type to parse to.</typeparam>
public sealed class ObjectSchema<T> : ObjectSchemaBase<T, ObjectSchema<T>>
{
    private readonly JsonTypeInfo<T> _typeInfo;

    internal ObjectSchema(SchemaNode node, JsonTypeInfo<T> typeInfo)
        : base(node) => _typeInfo = typeInfo;

    /// <summary>Adds rules to a string property.</summary>
    /// <param name="member">The property, such as <c>p => p.Name</c>.</param>
    /// <param name="configure">Adds the rules.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// schema.Property(p => p.Name, s => s.MinLength(1).MaxLength(100));
    /// </code>
    /// </example>
    [Pure]
    public ObjectSchema<T> Property(Expression<Func<T, string?>> member, Func<StringSchema, StringSchema> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        (string name, SchemaNode node) = Find(member);
        return UpdateProperty(name, configure(new StringSchema(node)).ToNode());
    }

    /// <summary>Adds rules to a boolean property.</summary>
    /// <param name="member">The property, such as <c>p => p.Active</c>.</param>
    /// <param name="configure">Adds the rules.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// schema.Property(p => p.Active, s => s.Default(true));
    /// </code>
    /// </example>
    [Pure]
    public ObjectSchema<T> Property(Expression<Func<T, bool>> member, Func<BooleanSchema, BooleanSchema> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        (string name, SchemaNode node) = Find(member);
        return UpdateProperty(name, configure(new BooleanSchema(node)).ToNode());
    }

    /// <summary>Adds rules to a numeric property.</summary>
    /// <typeparam name="TNumber">The property's type.</typeparam>
    /// <param name="member">The property, such as <c>p => p.Age</c>.</param>
    /// <param name="configure">Adds the rules.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// schema.Property(p => p.Age, s => s.Minimum(0).Maximum(150));
    /// </code>
    /// </example>
    [Pure]
    public ObjectSchema<T> Property<TNumber>(Expression<Func<T, TNumber>> member, Func<NumberSchema<TNumber>, NumberSchema<TNumber>> configure)
        where TNumber : struct, INumber<TNumber>
    {
        ArgumentNullException.ThrowIfNull(configure);
        (string name, SchemaNode node) = Find(member);
        return UpdateProperty(name, configure(new NumberSchema<TNumber>(node)).ToNode());
    }

    /// <summary>Adds rules to a nullable numeric property. <c>null</c> stays allowed.</summary>
    /// <typeparam name="TNumber">The property's underlying type.</typeparam>
    /// <param name="member">The property, such as <c>p => p.Height</c>.</param>
    /// <param name="configure">Adds the rules.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// schema.Property(p => p.Height, s => s.Minimum(0));
    /// </code>
    /// </example>
    [Pure]
    public ObjectSchema<T> Property<TNumber>(Expression<Func<T, TNumber?>> member, Func<NumberSchema<TNumber>, NumberSchema<TNumber>> configure)
        where TNumber : struct, INumber<TNumber>
    {
        ArgumentNullException.ThrowIfNull(configure);
        (string name, SchemaNode node) = Find(member);
        return UpdateProperty(name, configure(new NumberSchema<TNumber>(node)).ToNode());
    }

    /// <summary>Adds rules to a collection property.</summary>
    /// <typeparam name="TItem">The item type.</typeparam>
    /// <param name="member">The property, such as <c>p => p.Tags</c>.</param>
    /// <param name="configure">Adds the rules.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// schema.Property(p => p.Addresses, s => s.MinItems(1).Items(AddressSchema));
    /// </code>
    /// </example>
    [Pure]
    public ObjectSchema<T> Property<TItem>(Expression<Func<T, IEnumerable<TItem>?>> member, Func<ArraySchema<TItem>, ArraySchema<TItem>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        (string name, SchemaNode node) = Find(member);
        JsonSerializerOptions options = _typeInfo.Options;

        ArraySchema<TItem> seeded = new(
            node,
            item => (TItem)item.Deserialize(options.GetTypeInfo(typeof(TItem)))!,
            item => JsonSerializer.SerializeToElement(item, options.GetTypeInfo(typeof(TItem))));

        return UpdateProperty(name, configure(seeded).ToNode());
    }

    /// <summary>
    /// Replaces a property's schema, for nested objects and any other type. The property stays nullable if the
    /// .NET type says so.
    /// </summary>
    /// <typeparam name="TProperty">The property's type.</typeparam>
    /// <param name="member">The property, such as <c>p => p.Home</c>.</param>
    /// <param name="schema">The schema.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// schema.Property(p => p.Home, AddressSchema);
    /// </code>
    /// </example>
    [Pure]
    public ObjectSchema<T> Property<TProperty>(Expression<Func<T, TProperty?>> member, Schema<TProperty> schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        (string name, SchemaNode seeded) = Find(member);
        SchemaNode node = schema.ToNode();
        bool nullable = seeded.Types.HasFlag(JsonTypes.Null) || seeded.AnyOf.Any(branch => branch.Types == JsonTypes.Null);
        return UpdateProperty(name, nullable ? node.WithNull() : node);
    }

    /// <summary>
    /// Lets a property be absent. JSON Schema: removes it from <c>required</c>. Zod equivalent: <c>.optional()</c>.
    /// </summary>
    /// <typeparam name="TProperty">The property's type.</typeparam>
    /// <param name="member">The property.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// schema.Optional(p => p.Nickname);
    /// </code>
    /// </example>
    [Pure]
    public ObjectSchema<T> Optional<TProperty>(Expression<Func<T, TProperty>> member)
    {
        string name = FindRemovable(member);
        return With(Node with { Required = Node.Required.Remove(name) });
    }

    /// <summary>
    /// Stops accepting a property. The value keeps its .NET default. Use it for values the server sets, such as an id.
    /// Zod equivalent: <c>.omit()</c>.
    /// </summary>
    /// <typeparam name="TProperty">The property's type.</typeparam>
    /// <param name="member">The property.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// ObjectSchema&lt;Person&gt; create = schema.Omit(p => p.Id);
    /// </code>
    /// </example>
    [Pure]
    public ObjectSchema<T> Omit<TProperty>(Expression<Func<T, TProperty>> member)
    {
        return RemoveProperty(FindRemovable(member));
    }

    /// <summary>
    /// Adds a C# check across properties, reported at one property. Zod equivalent: <c>.refine()</c> with <c>path</c>.
    /// </summary>
    /// <typeparam name="TProperty">The property's type.</typeparam>
    /// <param name="predicate">Returns true when the value is valid.</param>
    /// <param name="message">The issue message.</param>
    /// <param name="path">The property the issue is reported at.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// schema.Refine(p => p.Age >= 18 || p.Nickname is null, "Minors cannot have a nickname", p => p.Nickname);
    /// </code>
    /// </example>
    [Pure]
    public ObjectSchema<T> Refine<TProperty>(Func<T, bool> predicate, string message, Expression<Func<T, TProperty>> path)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(message);
        (string name, _) = Find(path);
        string pointer = "/" + name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
        return AddRefinement(new SchemaRefinement<T>(Materialize, predicate, null, message, pointer, null));
    }

    private protected override ObjectSchema<T> With(SchemaNode node) => new(node, _typeInfo);

    internal override T Materialize(JsonElement value) => value.Deserialize(_typeInfo)!;

    internal override JsonElement ToElement(T value) => JsonSerializer.SerializeToElement(value, _typeInfo);

    /// <summary>System.Text.Json fails on a missing <c>required</c> member, so such a property can't be made optional.</summary>
    private string FindRemovable(LambdaExpression member)
    {
        (string name, _) = Find(member);

        return _typeInfo.Properties.Any(property => property.Name == name && property.IsRequired)
            ? throw new ArgumentException($"{name} is required by System.Text.Json, so it must stay in the input.", nameof(member))
            : name;
    }

    private (string Name, SchemaNode Node) Find(LambdaExpression member)
    {
        ArgumentNullException.ThrowIfNull(member);

        Expression body = member.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert ? convert.Operand : member.Body;

        if (body is not MemberExpression { Member: var clrMember, Expression: ParameterExpression })
        {
            throw new ArgumentException($"Expected a direct property of {typeof(T).Name}, such as p => p.Name.", nameof(member));
        }

        foreach (JsonPropertyInfo property in _typeInfo.Properties)
        {
            if (property.AttributeProvider is MemberInfo candidate && candidate.Name == clrMember.Name)
            {
                SchemaProperty? seeded = Node.Properties.FirstOrDefault(existing => existing.Name == property.Name);

                return seeded is not null
                    ? (property.Name, seeded.Schema)
                    : throw new ArgumentException($"{clrMember.Name} was removed from this schema.", nameof(member));
            }
        }

        throw new ArgumentException($"{clrMember.Name} isn't part of the JSON contract of {typeof(T).Name}.", nameof(member));
    }
}
