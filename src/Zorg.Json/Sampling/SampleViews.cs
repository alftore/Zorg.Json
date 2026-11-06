using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Zorg.Json.Model;
using Zorg.Json.Validation;

namespace Zorg.Json.Sampling;

/// <summary>One way to satisfy a schema: a node with no applicators left, and the references it went through.</summary>
/// <param name="Node">
/// The keywords that all apply. <see cref="SchemaNode.AllOf"/> holds what can't be merged: further patterns and <c>contains</c>.
/// </param>
/// <param name="Targets">The references followed.</param>
internal sealed record SampleView(SchemaNode Node, ImmutableArray<SchemaNode> Targets);

/// <summary>
/// Flattens a schema into <see cref="SampleView"/>s. <c>$ref</c>, <c>allOf</c> and a chosen branch of each
/// <c>anyOf</c>, <c>oneOf</c>, <c>if</c> and <c>dependentSchemas</c> merge into one set of keywords, so that a generated
/// value meets them together. A view only steers generation: candidates are validated against the original schema.
/// </summary>
internal sealed class SampleViews
{
    private const int MaxInPlaceDepth = 200;

    /// <summary>Stops a product of unions, which can have billions of combinations that all conflict.</summary>
    private const int MaxMerges = 40_000;

    private static readonly SampleView Anything = new(SchemaNode.True, []);

    private readonly Dictionary<string, Regex> _patterns = new(StringComparer.Ordinal);

    /// <summary>How many views were merged, which counts as work for the generator.</summary>
    public int Merges { get; private set; }

    public IEnumerable<SampleView> Of(SchemaNode node) => Of(node, null);

    /// <summary>What applies to a property of a flattened object.</summary>
    public SchemaNode ForName(SchemaNode view, string name) => All(Applying(view, name)) ?? SchemaNode.True;

    public bool Matches(string pattern, string name)
    {
        if (!_patterns.TryGetValue(pattern, out Regex? regex))
        {
            regex = PatternCompiler.Compile(pattern);
            _patterns.Add(pattern, regex);
        }

        try
        {
            return regex.IsMatch(name);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>The conjunction of some schemas, or null if none of them says anything.</summary>
    public static SchemaNode? All(IEnumerable<SchemaNode?> nodes)
    {
        List<SchemaNode> distinct = [];

        foreach (SchemaNode? node in nodes)
        {
            if (node is null || node.BooleanValue == true || distinct.Any(seen => ReferenceEquals(seen, node)))
            {
                continue;
            }

            if (node.BooleanValue == false)
            {
                return SchemaNode.False;
            }

            distinct.Add(node);
        }

        return distinct.Count switch
        {
            0 => null,
            1 => distinct[0],
            _ => new SchemaNode { AllOf = [.. distinct] },
        };
    }

    private static SchemaNode Base(SchemaNode node)
    {
        // Beside other subschemas these depend on what those evaluate, which a view can't tell. The finished sample is checked anyway.
        bool isolated = node.Ref is null && node.AllOf.IsEmpty && node.AnyOf.IsEmpty && node.OneOf.IsEmpty && node.Not is null && node.If is null && node.DependentSchemas.IsEmpty;
        SchemaNode? additional = node.AdditionalProperties ?? (isolated ? node.UnevaluatedProperties : null);

        if (additional is null && node.Behavior.UnknownProperties == UnknownProperties.Reject)
        {
            additional = SchemaNode.False;
        }

        return node with
        {
            Ref = null,
            Defs = [],
            AllOf = [],
            AnyOf = [],
            OneOf = [],
            Not = null,
            If = null,
            Then = null,
            Else = null,
            DependentSchemas = [],
            Items = node.Items ?? (isolated && node.Contains is null ? node.UnevaluatedItems : null),
            UnevaluatedItems = null,
            AdditionalProperties = additional,
            UnevaluatedProperties = null,
            Properties = node.Properties.Any(property => property.OutputOnly) ? [.. node.Properties.Where(property => !property.OutputOnly)] : node.Properties,
        };
    }

    /// <summary>A branch that only allows <c>null</c> comes last, so that a nullable value prefers its other type.</summary>
    private static IEnumerable<SchemaNode> NonNullFirst(ImmutableArray<SchemaNode> branches) =>
        branches.OrderBy(branch => branch.Types == JsonTypes.Null);

    private static JsonTypes Widen(JsonTypes types) => types.HasFlag(JsonTypes.Number) ? types | JsonTypes.Integer : types;

    private static JsonElement? Tighter(JsonElement? left, JsonElement? right, int direction)
    {
        if (left is not { } first || right is not { } second)
        {
            return left ?? right;
        }

        int order = ExactNumber.Parse(first.GetRawText()).CompareTo(ExactNumber.Parse(second.GetRawText()));
        return order * direction >= 0 ? first : second;
    }

    private static int? Larger(int? left, int? right) => left is { } first && right is { } second ? Math.Max(first, second) : left ?? right;

    private static int? Smaller(int? left, int? right) => left is { } first && right is { } second ? Math.Min(first, second) : left ?? right;

    private static SchemaNode? ItemAt(SchemaNode node, int index) => index < node.PrefixItems.Length ? node.PrefixItems[index] : node.Items;

    private static SchemaNode? Residue(SchemaNode left, SchemaNode right, out ImmutableArray<SchemaNode> residue, out int? minContains, out int? maxContains)
    {
        residue = [.. left.AllOf, .. right.AllOf];

        if (left.Pattern is not null && right.Pattern is not null && left.Pattern != right.Pattern)
        {
            residue = residue.Add(new SchemaNode { Pattern = right.Pattern });
        }

        if (left.Contains is not null && right.Contains is not null)
        {
            residue = residue.Add(new SchemaNode { Contains = right.Contains, MinContains = right.MinContains, MaxContains = right.MaxContains });
        }

        SchemaNode owner = left.Contains is null ? right : left;
        minContains = owner.MinContains;
        maxContains = owner.MaxContains;
        return owner.Contains;
    }

    private IEnumerable<SampleView> Of(SchemaNode node, Chain? path)
    {
        if (node.BooleanValue is { } allowed)
        {
            return allowed ? [Anything] : [];
        }

        // Branches nest as deep as the schema does, and each level of an enumeration takes several frames. A view only steers
        // generation, so when the stack runs low the rest is left open.
        if (path?.Depth > MaxInPlaceDepth || !System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            return [Anything];
        }

        List<Func<IEnumerable<SampleView>>> factors = [];

        if (node.Behavior.Target is { } target)
        {
            SchemaNode resolved = target();

            if (path?.Contains(resolved) != true)
            {
                factors.Add(() => Of(resolved, new Chain(resolved, path)).Select(view => view with { Targets = view.Targets.Add(resolved) }));
            }
        }

        foreach (SchemaNode branch in node.AllOf)
        {
            factors.Add(() => Of(branch, path));
        }

        if (!node.AnyOf.IsEmpty)
        {
            factors.Add(() => NonNullFirst(node.AnyOf).SelectMany(branch => Of(branch, path)));
        }

        if (!node.OneOf.IsEmpty)
        {
            factors.Add(() => NonNullFirst(node.OneOf).SelectMany(branch => Of(branch, path)));
        }

        if (node.If is { } condition && (node.Then is not null || node.Else is not null))
        {
            factors.Add(() => Conditional(condition, node.Then, node.Else, path));
        }

        foreach (SchemaProperty dependent in node.DependentSchemas)
        {
            factors.Add(() => Of(dependent.Schema, path).Append(Anything));
        }

        return Product(new SampleView(Base(node), []), factors, 0);
    }

    private IEnumerable<SampleView> Conditional(SchemaNode condition, SchemaNode? then, SchemaNode? otherwise, Chain? path)
    {
        foreach (SampleView matched in Of(condition, path))
        {
            foreach (SampleView view in then is null ? [Anything] : Of(then, path))
            {
                SchemaNode merged = Merge(matched.Node, view.Node);

                if (merged.BooleanValue != false)
                {
                    yield return new SampleView(merged, [.. matched.Targets, .. view.Targets]);
                }
            }
        }

        foreach (SampleView view in otherwise is null ? [Anything] : Of(otherwise, path))
        {
            yield return view;
        }
    }

    private IEnumerable<SampleView> Product(SampleView current, List<Func<IEnumerable<SampleView>>> factors, int index)
    {
        if (index == factors.Count)
        {
            yield return current;
            yield break;
        }

        foreach (SampleView option in factors[index]())
        {
            if (Merges > MaxMerges)
            {
                yield break;
            }

            SchemaNode merged = Merge(current.Node, option.Node);

            if (merged.BooleanValue == false)
            {
                continue;
            }

            foreach (SampleView view in Product(new SampleView(merged, [.. current.Targets, .. option.Targets]), factors, index + 1))
            {
                yield return view;
            }
        }
    }

    private SchemaNode Merge(SchemaNode left, SchemaNode right)
    {
        Merges++;

        if (left.BooleanValue == true)
        {
            return right;
        }

        if (right.BooleanValue == true)
        {
            return left;
        }

        JsonTypes types = left.Types == JsonTypes.None ? right.Types
            : right.Types == JsonTypes.None ? left.Types
            : Widen(left.Types) & Widen(right.Types);

        bool typesConflict = types == JsonTypes.None && left.Types != JsonTypes.None && right.Types != JsonTypes.None;
        bool constantsConflict = left.Const is { } first && right.Const is { } second && !JsonEquality.AreEqual(first, second);
        ImmutableArray<JsonElement> allowed = left.Enum.IsEmpty ? right.Enum
            : right.Enum.IsEmpty ? left.Enum
            : [.. left.Enum.Where(value => right.Enum.Any(other => JsonEquality.AreEqual(value, other)))];

        if (typesConflict || constantsConflict || (allowed.IsEmpty && !(left.Enum.IsEmpty && right.Enum.IsEmpty)))
        {
            return SchemaNode.False;
        }

        SchemaNode? contains = Residue(left, right, out ImmutableArray<SchemaNode> residue, out int? minContains, out int? maxContains);

        return left with
        {
            Types = types,
            Const = left.Const ?? right.Const,
            Enum = allowed,
            Default = left.Default ?? right.Default,
            Examples = [.. left.Examples, .. right.Examples],
            Minimum = Tighter(left.Minimum, right.Minimum, 1),
            Maximum = Tighter(left.Maximum, right.Maximum, -1),
            ExclusiveMinimum = Tighter(left.ExclusiveMinimum, right.ExclusiveMinimum, 1),
            ExclusiveMaximum = Tighter(left.ExclusiveMaximum, right.ExclusiveMaximum, -1),
            MultipleOf = left.MultipleOf is { } step && right.MultipleOf is { } other ? SampleNumbers.Lcm(step, other) ?? step : left.MultipleOf ?? right.MultipleOf,
            MinLength = Larger(left.MinLength, right.MinLength),
            MaxLength = Smaller(left.MaxLength, right.MaxLength),
            Pattern = left.Pattern ?? right.Pattern,
            Format = left.Format ?? right.Format,
            ContentEncoding = left.ContentEncoding ?? right.ContentEncoding,
            PrefixItems = MergePrefixItems(left, right),
            Items = All([left.Items, right.Items]),
            Contains = contains,
            MinContains = minContains,
            MaxContains = maxContains,
            MinItems = Larger(left.MinItems, right.MinItems),
            MaxItems = Smaller(left.MaxItems, right.MaxItems),
            UniqueItems = left.UniqueItems == true || right.UniqueItems == true ? true : null,
            Properties = MergeProperties(left, right),
            Required = [.. left.Required.Union(right.Required, StringComparer.Ordinal)],
            PatternProperties = [.. left.PatternProperties, .. right.PatternProperties],
            AdditionalProperties = All([left.AdditionalProperties, right.AdditionalProperties]),
            PropertyNames = All([left.PropertyNames, right.PropertyNames]),
            DependentRequired = [.. left.DependentRequired, .. right.DependentRequired],
            MinProperties = Larger(left.MinProperties, right.MinProperties),
            MaxProperties = Smaller(left.MaxProperties, right.MaxProperties),
            AllOf = residue,
        };
    }

    private static ImmutableArray<SchemaNode> MergePrefixItems(SchemaNode left, SchemaNode right)
    {
        int length = Math.Max(left.PrefixItems.Length, right.PrefixItems.Length);
        return [.. Enumerable.Range(0, length).Select(index => All([ItemAt(left, index), ItemAt(right, index)]) ?? SchemaNode.True)];
    }

    /// <summary>A name declared on one side still answers to the other side's patterns and <c>additionalProperties</c>.</summary>
    private ImmutableArray<SchemaProperty> MergeProperties(SchemaNode left, SchemaNode right)
    {
        if (left.Properties.IsEmpty && right.Properties.IsEmpty)
        {
            return [];
        }

        IEnumerable<string> names = left.Properties.Concat(right.Properties).Select(property => property.Name).Distinct(StringComparer.Ordinal);
        return [.. names.Select(name => new SchemaProperty(name, All(Applying(left, name).Concat(Applying(right, name))) ?? SchemaNode.True))];
    }

    private IEnumerable<SchemaNode?> Applying(SchemaNode node, string name)
    {
        bool covered = false;

        foreach (SchemaProperty property in node.Properties)
        {
            if (property.Name == name)
            {
                covered = true;
                yield return property.Schema;
            }
        }

        foreach (SchemaProperty property in node.PatternProperties)
        {
            if (Matches(property.Name, name))
            {
                covered = true;
                yield return property.Schema;
            }
        }

        if (!covered)
        {
            yield return node.AdditionalProperties;
        }
    }

    /// <summary>The references followed in place, so that a cycle with no value in between ends.</summary>
    private sealed record Chain(SchemaNode Node, Chain? Parent)
    {
        public int Depth { get; } = (Parent?.Depth ?? 0) + 1;

        public bool Contains(SchemaNode node)
        {
            for (Chain? link = this; link is not null; link = link.Parent)
            {
                if (ReferenceEquals(link.Node, node))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
