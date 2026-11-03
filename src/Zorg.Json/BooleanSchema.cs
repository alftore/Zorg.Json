using System.Diagnostics.Contracts;
using System.Text.Json;
using Zorg.Json.Model;

namespace Zorg.Json;

/// <summary>A schema for JSON booleans. Create one with <see cref="Z.Boolean"/>. Zod equivalent: <c>ZodBoolean</c>.</summary>
public sealed class BooleanSchema : Schema<bool, BooleanSchema>
{
    internal BooleanSchema(SchemaNode node)
        : base(node)
    {
    }

    /// <summary>Requires exactly this value. JSON Schema: <c>const</c>. Zod equivalent: <c>z.literal()</c>.</summary>
    /// <param name="value">The only allowed value.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// BooleanSchema schema = Z.Boolean().Const(true);
    /// </code>
    /// </example>
    [Pure]
    public BooleanSchema Const(bool value) => With(Node with { Const = ToElement(value) });

    /// <summary>
    /// Also accepts the strings <c>"true"</c> and <c>"false"</c>. The input schema adds <c>string</c> to <c>type</c>,
    /// with a <c>pattern</c>. Zod equivalent: <c>z.stringbool()</c>, which accepts more spellings.
    /// </summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// BooleanSchema schema = Z.Boolean().Coerce();
    /// </code>
    /// </example>
    [Pure]
    public BooleanSchema Coerce() => With(Node with { Behavior = Node.Behavior with { Coercion = SchemaCoercion.Boolean } });

    private protected override BooleanSchema With(SchemaNode node) => new(node);

    internal override bool Materialize(JsonElement value) => value.GetBoolean();

    internal override JsonElement ToElement(bool value) => JsonElement.Parse(value ? "true" : "false");
}
