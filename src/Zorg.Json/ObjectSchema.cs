using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text.Json;
using Zorg.Json.Model;

namespace Zorg.Json;

/// <summary>
/// Members shared by untyped and typed object schemas. Unknown properties are rejected until
/// <see cref="Strip"/> or <see cref="Passthrough"/> says otherwise.
/// </summary>
/// <typeparam name="T">The type of the parsed value.</typeparam>
/// <typeparam name="TSelf">The concrete builder.</typeparam>
public abstract class ObjectSchemaBase<T, TSelf> : Schema<T, TSelf>
    where TSelf : ObjectSchemaBase<T, TSelf>
{
    private protected ObjectSchemaBase(SchemaNode node)
        : base(node)
    {
    }

    /// <summary>Rejects unknown properties, which is already the default. JSON Schema: <c>additionalProperties: false</c>. Zod equivalent: <c>.strict()</c>.</summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// ObjectSchema schema = Z.Object().Strict();
    /// </code>
    /// </example>
    [Pure]
    public TSelf Strict() => WithUnknownProperties(UnknownProperties.Reject);

    /// <summary>
    /// Accepts unknown properties and removes them from the result. The input schema leaves <c>additionalProperties</c> out,
    /// and the output schema has <c>additionalProperties: false</c>. Zod equivalent: <c>.strip()</c>.
    /// </summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// ObjectSchema schema = Z.Object().Strip();
    /// </code>
    /// </example>
    [Pure]
    public TSelf Strip() => WithUnknownProperties(UnknownProperties.Strip);

    /// <summary>Accepts unknown properties and keeps them. JSON Schema: no <c>additionalProperties</c>. Zod equivalent: <c>.passthrough()</c>.</summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// ObjectSchema schema = Z.Object().Passthrough();
    /// </code>
    /// </example>
    [Pure]
    public TSelf Passthrough() => WithUnknownProperties(UnknownProperties.Allow);

    /// <summary>Validates unknown properties against a schema. JSON Schema: <c>additionalProperties</c>. Zod equivalent: <c>.catchall()</c>.</summary>
    /// <param name="schema">The schema for unknown properties.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// ObjectSchema schema = Z.Object().AdditionalProperties(Z.String());
    /// </code>
    /// </example>
    [Pure]
    public TSelf AdditionalProperties(ISchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return With(Node with { AdditionalProperties = schema.ToNode() });
    }

    /// <summary>Validates properties whose name matches a regular expression. JSON Schema: <c>patternProperties</c>.</summary>
    /// <param name="pattern">The name pattern.</param>
    /// <param name="schema">The schema for matching properties.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// ObjectSchema schema = Z.Object().PatternProperty("^x-", Z.String());
    /// </code>
    /// </example>
    [Pure]
    public TSelf PatternProperty(string pattern, ISchema schema)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(schema);
        _ = Validation.PatternCompiler.Compile(pattern);
        return With(Node with { PatternProperties = Node.PatternProperties.Add(new SchemaProperty(pattern, schema.ToNode())) });
    }

    /// <summary>Validates every property name. JSON Schema: <c>propertyNames</c>.</summary>
    /// <param name="schema">The schema for names.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// ObjectSchema schema = Z.Object().Passthrough().PropertyNames(Z.String().MaxLength(10));
    /// </code>
    /// </example>
    [Pure]
    public TSelf PropertyNames(StringSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return With(Node with { PropertyNames = schema.ToNode() });
    }

    /// <summary>Requires at least this many properties. JSON Schema: <c>minProperties</c>.</summary>
    /// <param name="count">The minimum count.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// ObjectSchema schema = Z.Object().Passthrough().MinProperties(1);
    /// </code>
    /// </example>
    [Pure]
    public TSelf MinProperties(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return With(Node with { MinProperties = count });
    }

    /// <summary>Allows at most this many properties. JSON Schema: <c>maxProperties</c>.</summary>
    /// <param name="count">The maximum count.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// ObjectSchema schema = Z.Object().Passthrough().MaxProperties(10);
    /// </code>
    /// </example>
    [Pure]
    public TSelf MaxProperties(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return With(Node with { MaxProperties = count });
    }

    /// <summary>Requires other properties when one is present. JSON Schema: <c>dependentRequired</c>.</summary>
    /// <param name="property">The property that triggers the rule.</param>
    /// <param name="required">The properties it needs.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// schema.DependentRequired("creditCard", "billingAddress");
    /// </code>
    /// </example>
    [Pure]
    public TSelf DependentRequired(string property, params ReadOnlySpan<string> required)
    {
        ArgumentNullException.ThrowIfNull(property);
        return With(Node with { DependentRequired = Node.DependentRequired.Add(KeyValuePair.Create(property, ImmutableArray.Create(required))) });
    }

    private protected TSelf ReplaceProperty(string name, SchemaNode schema, bool outputOnly = false)
    {
        SchemaProperty property = new(name, schema) { OutputOnly = outputOnly };
        int index = Node.Properties.ToList().FindIndex(existing => existing.Name == name);
        ImmutableArray<SchemaProperty> properties = index >= 0 ? Node.Properties.SetItem(index, property) : Node.Properties.Add(property);
        ImmutableArray<string> required = Node.Required.Remove(name);

        return With(Node with
        {
            Properties = properties,
            Required = schema.Behavior.IsOptional || outputOnly ? required : Insert(required, name, properties),
        });
    }

    /// <summary>Changes a property's rules, keeping whether it's required and whether it's output-only.</summary>
    private protected TSelf UpdateProperty(string name, SchemaNode schema)
    {
        int index = Node.Properties.ToList().FindIndex(existing => existing.Name == name);

        return With(Node with
        {
            Properties = Node.Properties.SetItem(index, Node.Properties[index] with { Schema = schema }),
            Required = schema.Behavior.IsOptional ? Node.Required.Remove(name) : Node.Required,
        });
    }

    private protected TSelf RemoveProperty(string name) => With(Node with
    {
        Properties = [.. Node.Properties.Where(property => property.Name != name)],
        Required = Node.Required.Remove(name),
    });

    private static ImmutableArray<string> Insert(ImmutableArray<string> required, string name, ImmutableArray<SchemaProperty> properties) =>
        [.. properties.Select(property => property.Name).Where(candidate => candidate == name || required.Contains(candidate))];

    private TSelf WithUnknownProperties(UnknownProperties handling) =>
        With(Node with { Behavior = Node.Behavior with { UnknownProperties = handling } });
}

/// <summary>
/// An untyped schema for JSON objects, parsing to a <see cref="JsonElement"/>. Create one with <see cref="Z.Object"/>.
/// Zod equivalent: <c>ZodObject</c>, except that unknown properties are rejected by default.
/// </summary>
public sealed class ObjectSchema : ObjectSchemaBase<JsonElement, ObjectSchema>
{
    internal ObjectSchema(SchemaNode node)
        : base(node)
    {
    }

    /// <summary>
    /// Adds a property, which is required unless its schema is <c>Optional</c> or has a <c>Default</c>.
    /// JSON Schema: <c>properties</c> and <c>required</c>. Zod equivalent: a key of <c>z.object()</c>.
    /// </summary>
    /// <param name="name">The JSON property name.</param>
    /// <param name="schema">The property's schema.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// ObjectSchema schema = Z.Object()
    ///     .Property("query", Z.String().MinLength(1))
    ///     .Property("limit", Z.Integer().Default(10));
    /// </code>
    /// </example>
    [Pure]
    public ObjectSchema Property(string name, ISchema schema)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(schema);
        return ReplaceProperty(name, schema.ToNode());
    }

    /// <summary>Keeps only these properties. Zod equivalent: <c>.pick()</c>.</summary>
    /// <param name="names">The properties to keep.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// ObjectSchema summary = user.Pick("id", "name");
    /// </code>
    /// </example>
    [Pure]
    public ObjectSchema Pick(params ReadOnlySpan<string> names)
    {
        ImmutableHashSet<string> keep = ImmutableHashSet.Create(StringComparer.Ordinal, names);

        return With(Node with
        {
            Properties = [.. Node.Properties.Where(property => keep.Contains(property.Name))],
            Required = [.. Node.Required.Where(keep.Contains)],
        });
    }

    /// <summary>Removes these properties. Zod equivalent: <c>.omit()</c>.</summary>
    /// <param name="names">The properties to remove.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// ObjectSchema create = user.Omit("id");
    /// </code>
    /// </example>
    [Pure]
    public ObjectSchema Omit(params ReadOnlySpan<string> names)
    {
        ObjectSchema result = this;

        foreach (string name in names)
        {
            result = result.RemoveProperty(name);
        }

        return result;
    }

    /// <summary>Makes every property optional. JSON Schema: removes <c>required</c>. Zod equivalent: <c>.partial()</c>.</summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// ObjectSchema patch = user.Partial();
    /// </code>
    /// </example>
    [Pure]
    public ObjectSchema Partial() => With(Node with { Required = [] });

    /// <summary>
    /// Adds the properties of another object, whose schema wins on a clash. The result is one flat object, because
    /// <c>additionalProperties: false</c> doesn't work inside <c>allOf</c>. Zod equivalent: <c>.extend()</c>.
    /// </summary>
    /// <param name="other">The object to merge in.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// ObjectSchema admin = user.Extend(Z.Object().Property("role", Z.String()));
    /// </code>
    /// </example>
    [Pure]
    public ObjectSchema Extend(ObjectSchema other)
    {
        ArgumentNullException.ThrowIfNull(other);
        ObjectSchema result = this;

        foreach (SchemaProperty property in other.Node.Properties)
        {
            result = result.ReplaceProperty(property.Name, property.Schema);

            if (!other.Node.Required.Contains(property.Name))
            {
                result = result.With(result.Node with { Required = result.Node.Required.Remove(property.Name) });
            }
        }

        return result;
    }

    /// <summary>Also allows JSON <c>null</c>. JSON Schema: adds <c>null</c> to <c>type</c>. Zod equivalent: <c>.nullable()</c>.</summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// ObjectSchema schema = Z.Object().Nullable();
    /// </code>
    /// </example>
    [Pure]
    public ObjectSchema Nullable() => With(Node.WithNull());

    private protected override ObjectSchema With(SchemaNode node) => new(node);

    internal override JsonElement Materialize(JsonElement value) => value.Clone();

    internal override JsonElement ToElement(JsonElement value) => value.Clone();
}
