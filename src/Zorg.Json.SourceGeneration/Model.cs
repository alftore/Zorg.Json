using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

#pragma warning disable IDE0130 // The namespaces are for the compiler only.
namespace System.Runtime.CompilerServices
{
    /// <summary>Lets records compile on netstandard2.0.</summary>
    internal static class IsExternalInit;
}

namespace System
{
    /// <summary>Lets list patterns compile on netstandard2.0.</summary>
    internal readonly struct Index(int value, bool fromEnd = false)
    {
        public int GetOffset(int length) => fromEnd ? length - value : value;

        public static implicit operator Index(int value) => new(value);
    }
}
#pragma warning restore IDE0130

namespace Zorg.Json.SourceGeneration
{
    /// <summary>An immutable array that compares by its items, so that the generator can tell that nothing changed.</summary>
    internal readonly struct EquatableArray<T>(ImmutableArray<T> items) : IEquatable<EquatableArray<T>>, IEnumerable<T>
        where T : IEquatable<T>
    {
        private readonly ImmutableArray<T> _items = items;

        private ImmutableArray<T> Items => _items.IsDefault ? [] : _items;

        public bool Equals(EquatableArray<T> other) => Items.SequenceEqual(other.Items);

        public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

        public override int GetHashCode()
        {
            int hash = 17;

            foreach (T item in Items)
            {
                hash = (hash * 31) + item.GetHashCode();
            }

            return hash;
        }

        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>A diagnostic that holds no symbols, so that it can be cached between runs.</summary>
    internal sealed record Finding(string Id, string? Argument, string? File, TextSpan Span, LinePositionSpan Lines)
    {
        public static Finding At(DiagnosticDescriptor descriptor, string? argument, Location? location)
        {
            FileLinePositionSpan lines = location?.GetLineSpan() ?? default;
            return new Finding(descriptor.Id, argument, location?.SourceTree?.FilePath, location?.SourceSpan ?? default, lines.Span);
        }

        public Diagnostic ToDiagnostic()
        {
            DiagnosticDescriptor descriptor = Descriptors.All.First(candidate => candidate.Id == Id);
            Location location = File is null ? Location.None : Location.Create(File, Span, Lines);
            return Diagnostic.Create(descriptor, location, Argument);
        }
    }

    /// <summary>One property with rules. <see cref="Nested"/> is the type with a generated schema that it holds, directly or as its items.</summary>
    internal sealed record PropertyModel(string Name, string Calls, string? Nested, bool NestedIsItem, string TypeDescription);

    /// <summary>What a type with <c>[GenerateSchema]</c> states.</summary>
    internal sealed record TypeModel(
        string FullName,
        string Hint,
        string? Namespace,
        string Declaration,
        string Name,
        string Context,
        string TypeCalls,
        EquatableArray<PropertyModel> Properties,
        EquatableArray<Finding> Findings,
        bool CanGenerate);
}
