using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Zorg.Json.Model;
using Zorg.Json.Typed;

namespace Zorg.Json;

/// <summary>
/// The typed entry point, for schemas bound to a .NET type. For dynamic JSON, and for scalars, use <see cref="Z"/>.
/// </summary>
public static class Schema
{
    /// <summary>
    /// Creates a schema from a type's System.Text.Json contract. It's AOT-safe and is the form to prefer. Every
    /// property is included, and unknown properties are removed, except that a type with <c>[JsonExtensionData]</c> or
    /// options with a <c>ReferenceHandler</c> keep them, and <c>JsonUnmappedMemberHandling.Disallow</c> rejects them. A
    /// name that differs only in case, or a computed property, is rejected when sent. A property is required if it's marked
    /// <see langword="required"/> or is a constructor parameter without a default.
    /// JSON Schema: <c>properties</c> and <c>required</c>.
    /// </summary>
    /// <typeparam name="T">The type to parse to.</typeparam>
    /// <param name="typeInfo">The contract, usually from a <c>JsonSerializerContext</c>.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// static readonly Schema&lt;Person&gt; PersonSchema = Schema.Object(AppJson.Default.Person)
    ///     .Property(p => p.Name, s => s.MinLength(1).MaxLength(100))
    ///     .Property(p => p.Age, s => s.Minimum(0).Maximum(150));
    /// </code>
    /// </example>
    public static ObjectSchema<T> Object<T>(JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        typeInfo.MakeReadOnly();
        return new ObjectSchema<T>(ContractSeeder.Seed(typeInfo), typeInfo);
    }

    /// <summary>
    /// Creates a schema using reflection. It's the short form for apps that don't use trimming or Native AOT.
    /// See <see cref="Object{T}(JsonTypeInfo{T})"/> for what it includes.
    /// </summary>
    /// <typeparam name="T">The type to parse to.</typeparam>
    /// <param name="options">The serializer options, which set names and converters. Default: <see cref="JsonSerializerOptions.Web"/>.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// ObjectSchema&lt;Person&gt; schema = Schema.Object&lt;Person&gt;();
    /// </code>
    /// </example>
    [RequiresUnreferencedCode("Uses reflection over T. Pass a JsonTypeInfo from a JsonSerializerContext instead.")]
    [RequiresDynamicCode("Uses reflection over T. Pass a JsonTypeInfo from a JsonSerializerContext instead.")]
    public static ObjectSchema<T> Object<T>(JsonSerializerOptions? options = null)
    {
        JsonSerializerOptions effective = options ?? JsonSerializerOptions.Web;
        effective.MakeReadOnly(populateMissingResolver: true);
        return Object((JsonTypeInfo<T>)effective.GetTypeInfo(typeof(T)));
    }

    /// <summary>
    /// Creates a union of .NET types selected by a JSON property. JSON Schema: <c>oneOf</c>, with a <c>const</c> in each
    /// branch. Zod equivalent: <c>z.discriminatedUnion()</c>. Add branches with <see cref="UnionSchema{T}.Case{TCase}"/>.
    /// </summary>
    /// <typeparam name="T">The base type of the cases.</typeparam>
    /// <param name="discriminator">The JSON property that selects the case.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// static readonly Schema&lt;Shape&gt; ShapeSchema = Schema.Union&lt;Shape&gt;("kind")
    ///     .Case("circle", CircleSchema)
    ///     .Case("square", SquareSchema);
    /// </code>
    /// </example>
    public static UnionSchema<T> Union<T>(string discriminator)
    {
        ArgumentNullException.ThrowIfNull(discriminator);
        Validation.Arguments.WellFormed(discriminator);
        return new UnionSchema<T>(new SchemaNode { Behavior = new SchemaBehavior { Discriminator = discriminator } }, []);
    }

    /// <summary>
    /// Creates a typed reference to a schema that isn't built yet, for recursive types. JSON Schema: <c>$ref</c> into
    /// <c>$defs</c>. Zod equivalent: <c>z.lazy()</c>.
    /// </summary>
    /// <typeparam name="T">The type the schema parses to.</typeparam>
    /// <param name="name">The name under <c>$defs</c>.</param>
    /// <param name="schema">Gets the schema on first use.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// static readonly Schema&lt;Category&gt; CategorySchema = Schema.Object(AppJson.Default.Category)
    ///     .Property(c => c.Children, s => s.Items(Schema.Lazy("category", () => CategorySchema)));
    /// </code>
    /// </example>
    public static LazySchema<T> Lazy<T>(string name, Func<Schema<T>> schema)
    {
        ArgumentNullException.ThrowIfNull(name);
        Validation.Arguments.WellFormed(name);
        ArgumentNullException.ThrowIfNull(schema);
        Lazy<Schema<T>> resolved = new(schema);

        return new LazySchema<T>(
            new SchemaNode
            {
                Ref = $"#/$defs/{name}",
                Behavior = new SchemaBehavior { DefinitionName = name, Target = () => resolved.Value.ToNode() },
            },
            resolved);
    }

    /// <summary>
    /// Creates a schema for a JSON string that parses to a .NET value. JSON Schema: <c>type: string</c> and
    /// <paramref name="format"/>. Zod equivalent: <c>z.iso.datetime()</c>, <c>z.uuid()</c> and similar, with a typed result.
    /// </summary>
    /// <typeparam name="T">The parsed type, such as <see cref="Guid"/> or <see cref="DateTimeOffset"/>.</typeparam>
    /// <param name="format">The <c>format</c> keyword, which is enforced if it's a known format.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// ParsableSchema&lt;Guid&gt; id = Schema.Parsable&lt;Guid&gt;("uuid");
    /// </code>
    /// </example>
    public static ParsableSchema<T> Parsable<T>(string? format = null)
        where T : IParsable<T>, IFormattable => new(new SchemaNode { Types = JsonTypes.String, Format = format });
}

/// <summary>A schema for a JSON string that parses to a .NET value. Create one with <see cref="Schema.Parsable{T}"/>.</summary>
/// <typeparam name="T">The parsed type.</typeparam>
public sealed class ParsableSchema<T> : Schema<T, ParsableSchema<T>>
    where T : IParsable<T>, IFormattable
{
    internal ParsableSchema(SchemaNode node)
        : base(node)
    {
    }

    private protected override ParsableSchema<T> With(SchemaNode node) => new(node);

    internal override T Materialize(JsonElement value) =>
        T.TryParse(Validation.JsonText.GetString(value), CultureInfo.InvariantCulture, out T? result)
            ? result
            : throw new JsonException($"The value isn't a valid {typeof(T).Name}.");

    internal override JsonElement ToElement(T value) =>
        Validation.JsonStrings.ToElement(value.ToString(null, CultureInfo.InvariantCulture));
}

/// <summary>A reference to a schema that isn't built yet. Create one with <see cref="Schema.Lazy{T}"/>.</summary>
/// <typeparam name="T">The type the schema parses to.</typeparam>
public sealed class LazySchema<T> : Schema<T, LazySchema<T>>
{
    private readonly Lazy<Schema<T>> _schema;

    internal LazySchema(SchemaNode node, Lazy<Schema<T>> schema)
        : base(node) => _schema = schema;

    private protected override LazySchema<T> With(SchemaNode node) => new(node, _schema);

    internal override T Materialize(JsonElement value) => _schema.Value.Materialize(value);

    internal override JsonElement ToElement(T value) => _schema.Value.ToElement(value);
}
