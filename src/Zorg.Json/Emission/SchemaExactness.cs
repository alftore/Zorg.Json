using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Zorg.Json.Model;
using Zorg.Json.Validation;

namespace Zorg.Json.Emission;

/// <summary>
/// Tells whether a subschema emits with exactly the verdicts it has in Zorg. Leaving a rule out makes a
/// schema looser, but under <c>not</c>, <c>oneOf</c>, <c>if</c> and <c>maxContains</c> a looser subschema makes the
/// document stricter, so those keywords may only be written around exact subschemas. What is left out depends on the
/// dialect: draft-07 has no <c>unevaluatedItems</c> and OpenAPI 3.0 has no <c>if</c>.
/// </summary>
internal static class SchemaExactness
{
    /// <summary>The most subschemas one walk visits. A reference that builds a fresh schema each time it is followed never ends otherwise.</summary>
    internal const int MaxWalked = 100_000;

    /// <summary>The most subschemas one search for a cause visits. Past it the cause names the keyword and not the place.</summary>
    private const int MaxLocateWalk = 10_000;

    /// <summary>The most subschemas all searches for a cause visit in one emission.</summary>
    private const int MaxLocateTotal = 100_000;

    private const int MaxPathSegments = 16;

    internal static InvalidOperationException TooLarge() => new(
        $"The schema reaches more than {MaxWalked} subschemas, usually because a Z.Lazy builds a new schema each time it is read. "
        + "Build the schema once, store it in a field, and refer to that.");

    /// <summary>
    /// Tells whether every subschema reachable from <paramref name="node"/> is exact. The walk is iterative, because a chain of
    /// references can be longer than the stack allows, and it follows each subschema once, so a cycle ends the walk.
    /// </summary>
    public static bool IsExact(SchemaNode node, JsonSchemaDialect dialect) => new Checker(dialect).IsExact(node);

    /// <summary>The first rule found that makes a subschema inexact, and where it sits.</summary>
    /// <param name="Keyword">The keyword, or the name of the fluent step, that isn't stated exactly, such as <c>format</c>.</param>
    /// <param name="Path">A JSON Pointer from the subschema that was checked. A followed reference reads as <c>$ref</c>. Empty for the subschema itself, and null when the walk to find it was cut short.</param>
    internal sealed record Cause(string Keyword, string? Path)
    {
        /// <summary>Reads as <c>format at /$defs/item/properties/id</c>, with <paramref name="prefix"/> for where the subschema sits.</summary>
        public string Describe(string prefix)
        {
            if (Path is null)
            {
                return prefix.Length == 0 ? $"{Keyword} somewhere in the schema" : $"{Keyword} somewhere below {prefix}";
            }

            string where = prefix + Path;
            return where.Length == 0 ? $"{Keyword} at the root" : $"{Keyword} at {where}";
        }
    }

    /// <summary>
    /// Remembers what it has judged. Whether a subschema is exact doesn't depend on where it's used, and a schema used a
    /// thousand times would otherwise be walked a thousand times.
    /// </summary>
    internal sealed class Checker(JsonSchemaDialect dialect, bool leavesOutDynamic = false)
    {
        private readonly Dictionary<SchemaNode, bool> _known = new(ReferenceEqualityComparer.Instance);

        private readonly Dictionary<SchemaNode, Cause?> _causes = new(ReferenceEqualityComparer.Instance);

        /// <summary>The keyword of the inexact rule a walk met, for each subschema that walk judged inexact.</summary>
        private readonly Dictionary<SchemaNode, string> _reasons = new(ReferenceEqualityComparer.Instance);

        /// <summary>Subschemas visited so far by walks that name a cause.</summary>
        private int _located;

        /// <summary>
        /// Tells whether <paramref name="root"/> is exact and, when it isn't, names the first rule found. Finding the rule
        /// walks again, but only for a subschema that is inexact, and only once for each.
        /// </summary>
        public bool IsExact(SchemaNode root, out Cause? cause)
        {
            if (IsExact(root))
            {
                cause = null;
                return true;
            }

            if (!_causes.TryGetValue(root, out cause))
            {
                _causes[root] = cause = Locate(root);
            }

            return false;
        }

        /// <summary>
        /// Walks in the order <see cref="IsExact(SchemaNode)"/> does, so it stops at the same first rule, and keeps the path.
        /// It stays cheap: it skips what is known to be exact, keeps parent links instead of copying paths, and gives up
        /// past a limit. Then the cause names only the keyword that <see cref="IsExact(SchemaNode)"/> found.
        /// </summary>
        private Cause Locate(SchemaNode root)
        {
            string fallbackKeyword = _reasons.GetValueOrDefault(root) ?? "rule";
            int allowed = Math.Min(MaxLocateWalk, MaxLocateTotal - _located);
            HashSet<SchemaNode> visited = new(ReferenceEqualityComparer.Instance);
            List<(int Parent, string Segment)> trail = [(-1, string.Empty)];
            Stack<(SchemaNode Node, int Index)> pending = new();
            pending.Push((root, 0));

            while (pending.TryPop(out (SchemaNode Node, int Index) item))
            {
                if (_known.TryGetValue(item.Node, out bool exact) && exact)
                {
                    continue;
                }

                if (!visited.Add(item.Node))
                {
                    continue;
                }

                if (visited.Count > allowed)
                {
                    _located += visited.Count;
                    return new Cause(fallbackKeyword, null);
                }

                if ((leavesOutDynamic && item.Node.DynamicRef is not null ? "$dynamicRef" : Inexact(item.Node, dialect)) is { } keyword)
                {
                    _located += visited.Count;
                    return new Cause(keyword, PathOf(trail, item.Index));
                }

                foreach ((string segment, SchemaNode child) in NamedChildren(item.Node))
                {
                    trail.Add((item.Index, segment));
                    pending.Push((child, trail.Count - 1));
                }

                if (item.Node.Behavior.Target is { } target)
                {
                    trail.Add((item.Index, "/$ref"));
                    pending.Push((target(), trail.Count - 1));
                }
            }

            // The walk judged this inexact, so a cause is always named.
            _located += visited.Count;
            return new Cause(fallbackKeyword, null);
        }

        /// <summary>Joins the segments from the root. A long path keeps its start and its end, so a diagnostic stays short.</summary>
        private static string PathOf(List<(int Parent, string Segment)> trail, int index)
        {
            List<string> segments = [];

            for (int at = index; at > 0; at = trail[at].Parent)
            {
                segments.Add(trail[at].Segment);
            }

            segments.Reverse();

            if (segments.Count <= MaxPathSegments)
            {
                return string.Concat(segments);
            }

            return string.Concat(segments.Take(MaxPathSegments / 2)) + "/..." + string.Concat(segments.Skip(segments.Count - MaxPathSegments / 2));
        }

        public bool IsExact(SchemaNode root)
        {
            if (_known.TryGetValue(root, out bool judged))
            {
                return judged;
            }

            HashSet<SchemaNode> visited = new(ReferenceEqualityComparer.Instance);
            List<(SchemaNode Node, int Parent)> trail = [(root, -1)];
            Stack<int> pending = new();
            pending.Push(0);

            while (pending.TryPop(out int index))
            {
                SchemaNode node = trail[index].Node;

                if (_known.TryGetValue(node, out bool known))
                {
                    if (known)
                    {
                        continue;
                    }

                    return Fail(trail, index, _reasons.GetValueOrDefault(node) ?? "rule");
                }

                if (!visited.Add(node))
                {
                    continue;
                }

                if (visited.Count > MaxWalked)
                {
                    throw TooLarge();
                }

                if ((leavesOutDynamic && node.DynamicRef is not null ? "$dynamicRef" : Inexact(node, dialect)) is { } keyword)
                {
                    return Fail(trail, index, keyword);
                }

                foreach (SchemaNode child in node.Children)
                {
                    trail.Add((child, index));
                    pending.Push(trail.Count - 1);
                }

                if (node.Behavior.Target is { } target)
                {
                    trail.Add((target(), index));
                    pending.Push(trail.Count - 1);
                }
            }

            // Every subschema the walk reached is exact, so none of them needs another walk.
            foreach (SchemaNode node in visited)
            {
                _known[node] = true;
            }

            return true;
        }

        /// <summary>
        /// Everything on the way from the root to an inexact subschema reaches it, so none of those needs another walk. A
        /// schema that refers many times to one long chain is then judged once.
        /// </summary>
        private bool Fail(List<(SchemaNode Node, int Parent)> trail, int index, string keyword)
        {
            for (int at = index; at >= 0; at = trail[at].Parent)
            {
                _known[trail[at].Node] = false;
                _reasons[trail[at].Node] = keyword;
            }

            return false;
        }
    }

    /// <summary>Names the first rule of <paramref name="node"/> that the emitted schema can't state exactly, or null when there is none.</summary>
    private static string? Inexact(SchemaNode node, JsonSchemaDialect dialect)
    {
        SchemaBehavior behavior = node.Behavior;

        if (!behavior.Normalizers.IsEmpty)
        {
            return behavior.Normalizers[0].Name;
        }

        if (!behavior.Refinements.IsEmpty)
        {
            return "refine";
        }

        if (behavior.Coercion != SchemaCoercion.None)
        {
            return "coerce";
        }

        if (behavior.NumberCheck is not null)
        {
            return "type";
        }

        if (behavior.PatternRegex is not null)
        {
            return "pattern";
        }

        if (behavior.IgnoresNameCase)
        {
            return "properties";
        }

        if (node.Format is not null && !behavior.FormatIsAnnotation && StringFormats.IsKnown(node.Format))
        {
            return "format";
        }

        if (CountsChange(node))
        {
            return node.MinProperties is not null ? "minProperties" : "maxProperties";
        }

        if (HasUnwritableValue(node))
        {
            return node.Const is { } constant && IsUnwritable(constant) ? "const" : "enum";
        }

        return LeavesOut(node, dialect);
    }

    /// <summary>The direct subschemas in the order of <see cref="SchemaNode.Children"/>, each with the pointer segment that leads to it.</summary>
    private static IEnumerable<(string Segment, SchemaNode Node)> NamedChildren(SchemaNode node)
    {
        static string Escape(string name) => name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

        foreach ((string keyword, ImmutableArray<SchemaProperty> list) in new[]
        {
            ("$defs", node.Defs), ("properties", node.Properties), ("patternProperties", node.PatternProperties), ("dependentSchemas", node.DependentSchemas),
        })
        {
            foreach (SchemaProperty property in list)
            {
                yield return ($"/{keyword}/{Escape(property.Name)}", property.Schema);
            }
        }

        foreach ((string keyword, ImmutableArray<SchemaNode> list) in new[]
        {
            ("prefixItems", node.PrefixItems), ("allOf", node.AllOf), ("anyOf", node.AnyOf), ("oneOf", node.OneOf),
        })
        {
            for (int i = 0; i < list.Length; i++)
            {
                yield return ($"/{keyword}/{i}", list[i]);
            }
        }

        (string, SchemaNode?)[] singles =
        [
            ("contentSchema", node.ContentSchema), ("items", node.Items), ("contains", node.Contains), ("additionalProperties", node.AdditionalProperties),
            ("propertyNames", node.PropertyNames), ("not", node.Not), ("if", node.If), ("then", node.Then), ("else", node.Else),
            ("unevaluatedItems", node.UnevaluatedItems), ("unevaluatedProperties", node.UnevaluatedProperties),
        ];

        foreach ((string keyword, SchemaNode? child) in singles)
        {
            if (child is not null)
            {
                yield return ($"/{keyword}", child);
            }
        }
    }

    /// <summary>Property counts apply after Strip and Default, which the input schema can't see, so it leaves them out.</summary>
    private static bool CountsChange(SchemaNode node) =>
        (node.MinProperties is not null || node.MaxProperties is not null)
        && (node.Behavior.UnknownProperties == UnknownProperties.Strip || node.Properties.Any(property => property.Schema.Behavior.FillDefault));

    /// <summary>A <c>const</c> or <c>enum</c> with text that isn't valid Unicode can't be written as JSON, so the emitter leaves it out.</summary>
    internal static bool HasUnwritableValue(SchemaNode node) =>
        (node.Const is { } constant && IsUnwritable(constant)) || node.Enum.Any(IsUnwritable);

    internal static bool IsUnwritable(System.Text.Json.JsonElement value) =>
        JsonText.HasUndecodableString(System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(value));

    /// <summary>The first keyword the emitter can't write in the dialect, or null. In OpenAPI 3.0 a null node with const or enum counts, because there is no null type and the values are written beside nullable alone.</summary>
    private static string? LeavesOut(SchemaNode node, JsonSchemaDialect dialect) => dialect switch
    {
        JsonSchemaDialect.Draft07 =>
            node.UnevaluatedItems is not null ? "unevaluatedItems"
            : node.UnevaluatedProperties is not null ? "unevaluatedProperties"
            : node.MinContains is not null ? "minContains"
            : node.MaxContains is not null ? "maxContains"
            : node.Extensions.Any(extension => extension.Key is "additionalItems") ? "additionalItems"
            : node.Extensions.Any(extension => extension.Key is "dependencies") ? "dependencies"
            : null,

        JsonSchemaDialect.OpenApi30 =>
            node.Types == JsonTypes.Null && node.Const is not null ? "const"
            : node.Types == JsonTypes.Null && !node.Enum.IsEmpty ? "enum"
            : !node.PrefixItems.IsEmpty ? "prefixItems"
            : node.Contains is not null ? "contains"
            : node.MinContains is not null ? "minContains"
            : node.MaxContains is not null ? "maxContains"
            : !node.PatternProperties.IsEmpty ? "patternProperties"
            : node.PropertyNames is not null ? "propertyNames"
            : !node.DependentRequired.IsEmpty ? "dependentRequired"
            : !node.DependentSchemas.IsEmpty ? "dependentSchemas"
            : node.Minimum is not null && node.ExclusiveMinimum is not null ? "exclusiveMinimum"
            : node.Maximum is not null && node.ExclusiveMaximum is not null ? "exclusiveMaximum"
            : node.Const is not null && !node.Enum.IsEmpty ? "const"
            : node.If is not null ? "if"
            : node.Then is not null ? "then"
            : node.Else is not null ? "else"
            : node.UnevaluatedItems is not null ? "unevaluatedItems"
            : node.UnevaluatedProperties is not null ? "unevaluatedProperties"
            : null,

        _ => null,
    };
}
