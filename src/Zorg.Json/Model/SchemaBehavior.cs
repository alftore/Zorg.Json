using System;
using System.Collections.Immutable;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Zorg.Json.Model;

/// <summary>The parts of a schema that have no JSON Schema keyword.</summary>
public sealed record SchemaBehavior
{
    internal static SchemaBehavior Empty { get; } = new();

    internal SchemaBehavior()
    {
    }

    /// <summary>Steps that change a string before its constraints are checked, in order.</summary>
    public ImmutableArray<SchemaNormalizer> Normalizers { get; init; } = [];

    /// <summary>C# checks that run after the constraints have passed, in order.</summary>
    public ImmutableArray<SchemaRefinement> Refinements { get; init; } = [];

    /// <summary>Which strings are accepted in place of the declared type.</summary>
    public SchemaCoercion Coercion { get; init; }

    /// <summary>What happens to properties no keyword covers. Imported schemas use <see cref="UnknownProperties.Allow"/>.</summary>
    public UnknownProperties UnknownProperties { get; init; } = UnknownProperties.Allow;

    /// <summary>Set by <c>Optional</c> and <c>Default</c>: the property isn't added to <c>required</c>.</summary>
    public bool IsOptional { get; init; }

    /// <summary>Whether parsing fills in <see cref="SchemaNode.Default"/> for an absent property.</summary>
    public bool FillDefault { get; init; }

    /// <summary>The property that selects the branch of a <c>oneOf</c>.</summary>
    public string? Discriminator { get; init; }

    /// <summary>The name under <c>$defs</c> when this node is hoisted or is a recursive reference.</summary>
    public string? DefinitionName { get; init; }

    /// <summary>What <see cref="SchemaNode.Ref"/> points at. Evaluated on first use, so that schemas can be recursive.</summary>
    public Func<SchemaNode>? Target { get; init; }

    internal bool FormatIsAnnotation { get; init; }

    /// <summary>Set by the fluent API, where Zorg enforces a format that other validators may only annotate.</summary>
    internal bool FormatIsFluent { get; init; }

    /// <summary>Set when the serializer matches names ignoring case, so <c>AGE</c> would bind to the property <c>age</c>.</summary>
    internal bool IgnoresNameCase { get; init; }

    internal Func<string, bool>? NumberCheck { get; init; }

    internal string? NumberType { get; init; }
}

/// <summary>Which strings a schema accepts in place of its declared type. Zod equivalent: <c>z.coerce</c>.</summary>
public enum SchemaCoercion
{
    /// <summary>None.</summary>
    None,

    /// <summary>Strings holding a JSON number, such as <c>"42"</c>.</summary>
    Number,

    /// <summary>The strings <c>"true"</c> and <c>"false"</c>.</summary>
    Boolean,
}

/// <summary>What happens to object properties that no keyword covers.</summary>
public enum UnknownProperties
{
    /// <summary>Keep them. JSON Schema's own default, and the one imported schemas use.</summary>
    Allow,

    /// <summary>Fail with <see cref="IssueCodes.UnrecognizedKeys"/>. The default for fluent schemas.</summary>
    Reject,

    /// <summary>Remove them from the result.</summary>
    Strip,
}

/// <summary>A step that changes a string before its constraints are checked.</summary>
/// <param name="Name">The fluent method that added the step, such as <c>Trim</c>.</param>
/// <param name="Apply">The change itself.</param>
public sealed record SchemaNormalizer(string Name, Func<string, string> Apply);

/// <summary>A C# check that JSON Schema can't express.</summary>
public abstract class SchemaRefinement
{
    private protected SchemaRefinement(string message, string path, string? hint)
    {
        Message = message;
        Path = path;
        Hint = hint;
    }

    /// <summary>The issue message when the check fails.</summary>
    public string Message { get; }

    /// <summary>A JSON Pointer, relative to the refined value, for the issue. Empty for the value itself.</summary>
    public string Path { get; }

    /// <summary>An explanation for the emitted schema, in place of <see cref="Message"/>.</summary>
    public string? Hint { get; }

    internal abstract bool IsAsync { get; }

    internal abstract bool Check(JsonElement value);

    internal abstract ValueTask<bool> CheckAsync(JsonElement value, CancellationToken cancellationToken);
}

internal static class Deserialization
{
    /// <summary>What System.Text.Json and <c>Parse</c> throw for a value they can't read. A caller's own exceptions pass through.</summary>
    public static bool Failed(Exception exception) =>
        exception is JsonException or NotSupportedException or InvalidOperationException or FormatException or OverflowException;
}

internal sealed class SchemaRefinement<T>(
    Func<JsonElement, T> materialize,
    Func<T, bool>? predicate,
    Func<T, CancellationToken, ValueTask<bool>>? asyncPredicate,
    string message,
    string path,
    string? hint) : SchemaRefinement(message, path, hint)
{
    internal override bool IsAsync => asyncPredicate is not null;

    internal override bool Check(JsonElement value) => predicate!(Read(value));

    internal override ValueTask<bool> CheckAsync(JsonElement value, CancellationToken cancellationToken) =>
        asyncPredicate!(Read(value), cancellationToken);

    internal SchemaRefinement<T> WithMaterializer(Func<JsonElement, T> replacement) =>
        new(replacement, predicate, asyncPredicate, Message, Path, Hint);

    private T Read(JsonElement value)
    {
        try
        {
            return materialize(value);
        }
        catch (Exception exception) when (Deserialization.Failed(exception) && exception is not JsonException)
        {
            throw new JsonException(exception.Message, exception);
        }
    }
}
