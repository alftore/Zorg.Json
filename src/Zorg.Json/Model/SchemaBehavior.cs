using System;
using System.Collections.Immutable;

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
}

/// <summary>A step that changes a string before its constraints are checked.</summary>
/// <param name="Name">The fluent method that added the step, such as <c>Trim</c>.</param>
/// <param name="Apply">The change itself.</param>
public sealed record SchemaNormalizer(string Name, Func<string, string> Apply);

/// <summary>A C# check that JSON Schema can't express.</summary>
public abstract class SchemaRefinement
{
    private protected SchemaRefinement(string message) => Message = message;

    /// <summary>The issue message when the check fails.</summary>
    public string Message { get; }
}

internal sealed class SchemaRefinement<T>(Func<T, bool> predicate, string message) : SchemaRefinement(message)
{
    public Func<T, bool> Predicate { get; } = predicate;
}
