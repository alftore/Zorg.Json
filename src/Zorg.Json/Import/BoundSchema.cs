using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Zorg.Json.Model;

namespace Zorg.Json.Import;

/// <summary>Validates with an imported node, then deserializes with System.Text.Json.</summary>
internal sealed class BoundSchema<T>(SchemaNode node, JsonTypeInfo<T> typeInfo) : Schema<T>(node)
{
    internal override T Materialize(JsonElement value) => value.Deserialize(typeInfo)!;

    internal override JsonElement ToElement(T value) => JsonSerializer.SerializeToElement(value, typeInfo);
}
