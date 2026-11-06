using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Zorg.Json.Model;
using Zorg.Json.Validation;

namespace Zorg.Json.Emission;

/// <summary>
/// Tells whether two object branches of a <c>oneOf</c> exclude each other as they are written, whatever rules a validator
/// doesn't apply. One branch must require a property that the other forbids, or give it values the other doesn't allow.
/// Only <c>required</c>, <c>properties</c>, <c>additionalProperties</c>, <c>const</c> and <c>enum</c> count, because every
/// validator applies those. Reads the target of a reference and not what sits beside it: a part of a branch that excludes
/// the other is enough, and some readers lose what sits beside a <c>$ref</c>. When in doubt the answer is that they can overlap.
/// </summary>
internal sealed class ObjectBranchCheck(bool isOpenApi30)
{
    /// <summary>The most comparisons one emission spends. Past it branches count as overlapping, so a large schema stays cheap to write.</summary>
    internal const long MaxSteps = 2_000_000;

    private const int MaxHops = 64;

    private long _steps;

    /// <summary>Reads what a branch states, or returns null when that isn't known.</summary>
    public ObjectBranch? Read(SchemaNode branch)
    {
        SchemaNode? node = Resolve(branch);

        if (node is null || node.Behavior.IgnoresNameCase || !Spend(node.Properties.Length + node.Required.Length))
        {
            return null;
        }

        Dictionary<string, SchemaNode> properties = new(StringComparer.Ordinal);

        foreach (SchemaProperty property in node.Properties)
        {
            // An input schema leaves these out.
            if (!property.OutputOnly)
            {
                properties[property.Name] = property.Schema;
            }
        }

        bool closed = node.PatternProperties.IsEmpty && (node.AdditionalProperties is { } additional
            ? IsFalse(additional)
            : node.Behavior.UnknownProperties == UnknownProperties.Reject);

        return new ObjectBranch(node.Required, properties, closed);
    }

    /// <summary>True when no object can match both branches.</summary>
    public bool Exclude(ObjectBranch left, ObjectBranch right) => Refuses(left, right) || Refuses(right, left);

    /// <summary>True when <paramref name="other"/> can't hold a property that <paramref name="branch"/> requires.</summary>
    private bool Refuses(ObjectBranch branch, ObjectBranch other)
    {
        foreach (string name in branch.Required)
        {
            if (!Spend(1))
            {
                return false;
            }

            if (!other.Properties.TryGetValue(name, out SchemaNode? theirs))
            {
                if (other.Closed)
                {
                    return true;
                }

                continue;
            }

            if (IsFalse(theirs))
            {
                return true;
            }

            if (branch.Properties.TryGetValue(name, out SchemaNode? ours)
                && WrittenValues(ours) is { } left && WrittenValues(theirs) is { } right && !Share(left, right))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The values a property is limited to as written, or null when that isn't known.</summary>
    private JsonElement[]? WrittenValues(SchemaNode schema)
    {
        SchemaNode node = schema;

        for (int hops = 0; ; hops++)
        {
            // These run before const and enum, which are then left out of an input schema.
            if (hops > MaxHops || !node.Behavior.Normalizers.IsEmpty || node.Behavior.Coercion != SchemaCoercion.None || node.DynamicRef is not null)
            {
                return null;
            }

            if (node.Behavior.Target is not { } target)
            {
                break;
            }

            node = target();
        }

        if (SchemaExactness.HasUnwritableValue(node))
        {
            return null;
        }

        // When both are there OpenAPI 3.0 writes only the enum, and both apply elsewhere, so the enum holds in every dialect.
        JsonElement[] values = !node.Enum.IsEmpty ? [.. node.Enum] : node.Const is { } constant ? [constant] : [];

        if (values.Length == 0 || !Spend(values.Length))
        {
            return null;
        }

        // OpenAPI 3.0 spells null with nullable, and readers differ on how that meets an enum.
        if (isOpenApi30 && ((node.Types & JsonTypes.Null) != JsonTypes.None || values.Any(value => value.ValueKind == JsonValueKind.Null)))
        {
            return null;
        }

        return values;
    }

    /// <summary>True when a value of one list may equal a value of the other in some validator.</summary>
    private bool Share(JsonElement[] left, JsonElement[] right)
    {
        if (!Spend((long)left.Length * right.Length))
        {
            return true;
        }

        return left.Any(value => right.Any(other => MayBeEqual(value, other)));
    }

    /// <summary>Some validators read numbers as doubles, so numbers that a double can't tell apart count as equal, and so do any two arrays or objects.</summary>
    private static bool MayBeEqual(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        if (left.ValueKind == JsonValueKind.Number)
        {
            return !left.TryGetDouble(out double first) || !right.TryGetDouble(out double second) || first == second;
        }

        return left.ValueKind != JsonValueKind.String || JsonEquality.AreEqual(left, right);
    }

    private static bool IsFalse(SchemaNode schema) => Resolve(schema)?.BooleanValue == false;

    private static SchemaNode? Resolve(SchemaNode node)
    {
        SchemaNode current = node;

        for (int hops = 0; hops <= MaxHops; hops++)
        {
            if (current.DynamicRef is not null)
            {
                return null;
            }

            if (current.Behavior.Target is not { } target)
            {
                return current;
            }

            current = target();
        }

        return null;
    }

    private bool Spend(long steps)
    {
        _steps += steps;
        return _steps <= MaxSteps;
    }
}

/// <summary>What a branch states about objects.</summary>
/// <param name="Required">The names in <c>required</c>.</param>
/// <param name="Properties">The written entries of <c>properties</c>.</param>
/// <param name="Closed">True when a name outside <see cref="Properties"/> is forbidden.</param>
internal sealed record ObjectBranch(IReadOnlyList<string> Required, Dictionary<string, SchemaNode> Properties, bool Closed);
