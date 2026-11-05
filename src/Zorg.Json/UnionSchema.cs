using System;
using System.Collections.Immutable;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text.Json;
using Zorg.Json.Model;
using Zorg.Json.Validation;

namespace Zorg.Json;

/// <summary>
/// A union of .NET types selected by a JSON property. Create one with <see cref="Schema.Union{T}"/>. It doesn't need
/// <c>[JsonPolymorphic]</c>, because each case parses with its own schema. Zod equivalent: <c>ZodDiscriminatedUnion</c>.
/// </summary>
/// <typeparam name="T">The base type of the cases.</typeparam>
public sealed class UnionSchema<T> : Schema<T, UnionSchema<T>>
{
    private readonly ImmutableDictionary<string, UnionCase> _cases;

    internal UnionSchema(SchemaNode node, ImmutableDictionary<string, UnionCase> cases)
        : base(node) => _cases = cases;

    /// <summary>
    /// Adds a case. JSON Schema: a branch of <c>oneOf</c> where the discriminator is a required <c>const</c>. The case's
    /// schema doesn't declare the discriminator.
    /// </summary>
    /// <typeparam name="TCase">The case's type.</typeparam>
    /// <param name="value">The discriminator value.</param>
    /// <param name="schema">The case's schema.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// schema.Case("circle", CircleSchema);
    /// </code>
    /// </example>
    [Pure]
    public UnionSchema<T> Case<TCase>(string value, Schema<TCase> schema)
        where TCase : T
    {
        ArgumentNullException.ThrowIfNull(value);
        Validation.Arguments.WellFormed(value);
        ArgumentNullException.ThrowIfNull(schema);

        string discriminator = Node.Behavior.Discriminator!;
        SchemaNode branch = schema.ToNode();
        SchemaProperty selector = new(discriminator, new SchemaNode { Const = JsonStrings.ToElement(value) });

        SchemaNode node = Node with
        {
            OneOf = Node.OneOf.Add(branch with
            {
                Properties = [selector, .. branch.Properties.Where(property => property.Name != discriminator)],
                Required = [discriminator, .. branch.Required.Where(name => name != discriminator)],
            }),
        };

        UnionCase union = new(element => schema.Materialize(element), item => schema.ToElement((TCase)item!), typeof(TCase));
        UnionSchema<T> result = new(node, _cases.Add(value, union));

        // A refinement parses with the schema it was added to, which doesn't know this case.
        return result.With(node with
        {
            Behavior = node.Behavior with
            {
                Refinements = [.. node.Behavior.Refinements.Select(refinement => ((SchemaRefinement<T>)refinement).WithMaterializer(result.Materialize))],
            },
        });
    }

    private protected override UnionSchema<T> With(SchemaNode node) => new(node, _cases);

    internal override T Materialize(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object
            && Validation.JsonText.TryGetProperty(value, Node.Behavior.Discriminator!, out JsonElement selected)
            && selected.ValueKind == JsonValueKind.String
            && _cases.TryGetValue(Validation.JsonText.GetString(selected), out UnionCase? union))
        {
            return union.Materialize(value);
        }

        throw new JsonException("The value doesn't select a case of this union.");
    }

    internal override JsonElement ToElement(T value)
    {
        string discriminator = Node.Behavior.Discriminator!;

        foreach ((string name, UnionCase union) in _cases)
        {
            if (union.Type == value?.GetType())
            {
                JsonElement element = union.ToElement(value);
                return JsonStrings.ToObject(element.EnumerateObject()
                    .Where(property => property.Name != discriminator)
                    .Select(property => System.Collections.Generic.KeyValuePair.Create(property.Name, property.Value))
                    .Prepend(System.Collections.Generic.KeyValuePair.Create(discriminator, JsonStrings.ToElement(name))));
            }
        }

        throw new ArgumentException($"{value?.GetType().Name} isn't a case of this union.", nameof(value));
    }

    internal sealed record UnionCase(Func<JsonElement, T> Materialize, Func<T, JsonElement> ToElement, Type Type);
}
