using System.Collections.Immutable;
using System.Text.Json;

namespace Zorg.Json.Model;

/// <summary>
/// The immutable model behind every schema. Its properties are JSON Schema keywords, plus
/// <see cref="Behavior"/> for what has no keyword. Read it with <see cref="Schema{T}.ToNode"/>.
/// </summary>
public sealed record SchemaNode
{
    internal SchemaNode()
    {
    }

    /// <summary>The <c>title</c> keyword.</summary>
    public string? Title { get; init; }

    /// <summary>The <c>description</c> keyword.</summary>
    public string? Description { get; init; }

    /// <summary>The <c>type</c> keyword.</summary>
    public JsonTypes Types { get; init; }

    /// <summary>The <c>const</c> keyword.</summary>
    public JsonElement? Const { get; init; }

    /// <summary>The <c>enum</c> keyword. Empty when absent.</summary>
    public ImmutableArray<JsonElement> Enum { get; init; } = [];

    /// <summary>The <c>minLength</c> keyword, in Unicode code points.</summary>
    public int? MinLength { get; init; }

    /// <summary>The <c>maxLength</c> keyword, in Unicode code points.</summary>
    public int? MaxLength { get; init; }

    /// <summary>The <c>pattern</c> keyword.</summary>
    public string? Pattern { get; init; }

    /// <summary>The <c>format</c> keyword.</summary>
    public string? Format { get; init; }

    /// <summary>Behaviour that has no JSON Schema keyword.</summary>
    public SchemaBehavior Behavior { get; init; } = SchemaBehavior.Empty;
}
