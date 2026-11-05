using System.Collections.Generic;
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
    /// <summary>
    /// Tells whether every subschema reachable from <paramref name="node"/> is exact. The walk is iterative, because a chain of
    /// references can be longer than the stack allows, and it follows each subschema once, so a cycle ends the walk.
    /// </summary>
    public static bool IsExact(SchemaNode node, JsonSchemaDialect dialect) => new Checker(dialect).IsExact(node);

    /// <summary>
    /// Remembers what it has judged. Whether a subschema is exact doesn't depend on where it's used, and a schema used a
    /// thousand times would otherwise be walked a thousand times.
    /// </summary>
    internal sealed class Checker(JsonSchemaDialect dialect)
    {
        private readonly Dictionary<SchemaNode, bool> _known = new(ReferenceEqualityComparer.Instance);

        public bool IsExact(SchemaNode root)
        {
            if (_known.TryGetValue(root, out bool judged))
            {
                return judged;
            }

            HashSet<SchemaNode> visited = new(ReferenceEqualityComparer.Instance);
            Stack<SchemaNode> pending = new();
            pending.Push(root);

            while (pending.TryPop(out SchemaNode? node))
            {
                if (_known.TryGetValue(node, out bool known))
                {
                    if (known)
                    {
                        continue;
                    }

                    _known[root] = false;
                    return false;
                }

                if (!visited.Add(node))
                {
                    continue;
                }

                if (!IsLocallyExact(node, dialect))
                {
                    _known[node] = false;
                    _known[root] = false;
                    return false;
                }

                foreach (SchemaNode child in node.Children)
                {
                    pending.Push(child);
                }

                if (node.Behavior.Target is { } target)
                {
                    pending.Push(target());
                }
            }

            // Every subschema the walk reached is exact, so none of them needs another walk.
            foreach (SchemaNode node in visited)
            {
                _known[node] = true;
            }

            return true;
        }
    }

    private static bool IsLocallyExact(SchemaNode node, JsonSchemaDialect dialect)
    {
        SchemaBehavior behavior = node.Behavior;

        return behavior.Normalizers.IsEmpty
            && behavior.Refinements.IsEmpty
            && behavior.Coercion == SchemaCoercion.None
            && behavior.NumberCheck is null
            && behavior.PatternRegex is null
            && !behavior.IgnoresNameCase
            && !(node.Format is not null && !behavior.FormatIsAnnotation && StringFormats.IsKnown(node.Format))
            && !CountsChange(node)
            && !HasUnwritableValue(node)
            && !LeavesOut(node, dialect);
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

    /// <summary>Keywords the emitter can't write in the dialect.</summary>
    private static bool LeavesOut(SchemaNode node, JsonSchemaDialect dialect) => dialect switch
    {
        JsonSchemaDialect.Draft07 =>
            node.UnevaluatedItems is not null
            || node.UnevaluatedProperties is not null
            || node.MinContains is not null
            || node.MaxContains is not null
            || node.Extensions.Any(extension => extension.Key is "additionalItems" or "dependencies"),

        JsonSchemaDialect.OpenApi30 =>
            !node.PrefixItems.IsEmpty
            || node.Contains is not null
            || node.MinContains is not null
            || node.MaxContains is not null
            || !node.PatternProperties.IsEmpty
            || node.PropertyNames is not null
            || !node.DependentRequired.IsEmpty
            || !node.DependentSchemas.IsEmpty
            || (node.Minimum is not null && node.ExclusiveMinimum is not null)
            || (node.Maximum is not null && node.ExclusiveMaximum is not null)
            || (node.Const is not null && !node.Enum.IsEmpty)
            || node.If is not null
            || node.Then is not null
            || node.Else is not null
            || node.UnevaluatedItems is not null
            || node.UnevaluatedProperties is not null,

        _ => false,
    };
}
