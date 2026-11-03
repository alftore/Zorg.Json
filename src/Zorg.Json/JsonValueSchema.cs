using System;
using System.Diagnostics.Contracts;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Zorg.Json.Import;
using Zorg.Json.Model;

namespace Zorg.Json;

/// <summary>
/// A schema that parses to a <see cref="JsonElement"/>, for anything without its own builder: unions, tuples and
/// imported documents. Create one with <see cref="Z"/>.
/// </summary>
public sealed class JsonValueSchema : Schema<JsonElement, JsonValueSchema>
{
    internal JsonValueSchema(SchemaNode node)
        : base(node)
    {
    }

    /// <summary>
    /// Creates a schema that validates like this one, then deserializes with System.Text.Json. The type isn't checked
    /// against the schema, and a JSON <c>null</c> the schema allows becomes <see langword="null"/>, as it does in System.Text.Json.
    /// JSON Schema: no keyword of its own. Zod equivalent: <c>.pipe()</c> into a typed schema.
    /// </summary>
    /// <typeparam name="T">The type to deserialize to.</typeparam>
    /// <param name="typeInfo">The contract, usually from a source-generated context.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// Schema&lt;WeatherArgs&gt; arguments = Z.FromJsonSchema(tool.InputSchema).Bind(AppJson.Default.WeatherArgs);
    /// </code>
    /// </example>
    [Pure]
    public Schema<T> Bind<T>(JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return new BoundSchema<T>(Node, typeInfo);
    }

    private protected override JsonValueSchema With(SchemaNode node) => new(node);

    internal override JsonElement Materialize(JsonElement value) => value.Clone();

    internal override JsonElement ToElement(JsonElement value) => value.Clone();
}
