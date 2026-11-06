using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    /// <summary>Set in Draft 2019-09, where <c>contains</c> evaluates no items for <c>unevaluatedItems</c>.</summary>
    internal bool ContainsEvaluatesNothing { get; init; }

    /// <summary>The resource an imported node belongs to, set only in a document that uses <c>$dynamicRef</c>.</summary>
    internal SchemaResource? Resource { get; init; }

    /// <summary>Where an imported node sits in its document, as a JSON Pointer. Null for fluent schemas.</summary>
    internal string? Location { get; init; }

    internal bool FormatIsAnnotation { get; init; }

    /// <summary>Set by the fluent API, where Zorg enforces a format that other validators may only annotate.</summary>
    internal bool FormatIsFluent { get; init; }

    /// <summary>Set when the serializer matches names ignoring case, so <c>AGE</c> would bind to the property <c>age</c>.</summary>
    internal bool IgnoresNameCase { get; init; }

    /// <summary>The regular expression a caller supplied to <c>Pattern(Regex)</c>. Validation uses it as given.</summary>
    internal Regex? PatternRegex { get; init; }

    /// <summary>Set when <see cref="PatternRegex"/> came without an ECMA-262 pattern, so <c>pattern</c> can't be emitted.</summary>
    internal bool PatternIsUnstated { get; init; }

    internal Func<string, bool>? NumberCheck { get; init; }

    internal string? NumberType { get; init; }
}

/// <summary>A schema resource, as the dynamic scope of <c>$dynamicRef</c> sees it: what each <c>$dynamicAnchor</c> in it names.</summary>
internal sealed class SchemaResource
{
    public Dictionary<string, SchemaNode> DynamicAnchors { get; } = new(StringComparer.Ordinal);
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

    /// <summary>Fail with <see cref="IssueCodes.UnrecognizedKeys"/>.</summary>
    Reject,

    /// <summary>Remove them from the result. The default for fluent schemas.</summary>
    Strip,
}

/// <summary>A step that changes a string before its constraints are checked.</summary>
/// <param name="Name">The fluent method that added the step, such as <c>Trim</c>.</param>
/// <param name="Apply">The change itself.</param>
public sealed record SchemaNormalizer(string Name, Func<string, string> Apply)
{
    /// <summary>Says whether a text is cheap enough to apply this step to. When it isn't, the parse fails as too complex.</summary>
    internal Func<string, bool>? Admits { get; init; }
}

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
        catch (Exception exception) when (Deserialization.Failed(exception))
        {
            throw new MaterializationException(exception.Message, exception);
        }
    }
}

/// <summary>
/// The value passed validation but couldn't be read as the .NET type. It is separate from a <see cref="JsonException"/>, so that
/// one thrown by a caller's own check isn't mistaken for it.
/// </summary>
#pragma warning disable CA1032
internal sealed class MaterializationException(string message, Exception inner) : JsonException(message, inner);
#pragma warning restore CA1032
