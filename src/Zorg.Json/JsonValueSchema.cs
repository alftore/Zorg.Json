using System.Text.Json;
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

    private protected override JsonValueSchema With(SchemaNode node) => new(node);

    internal override JsonElement Materialize(JsonElement value) => value.Clone();

    internal override JsonElement ToElement(JsonElement value) => value.Clone();
}
