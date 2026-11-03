using System.Collections.Generic;
using System.Linq;
using Zorg.Json.Model;
using Zorg.Json.Validation;

namespace Zorg.Json.Emission;

/// <summary>
/// Tells whether a subschema emits with exactly the verdicts it has in Zorg. Leaving a rule out makes a
/// schema looser, but under <c>not</c>, <c>oneOf</c>, <c>if</c> and <c>maxContains</c> a looser subschema makes the
/// document stricter, so those keywords may only be written around exact subschemas.
/// </summary>
internal static class SchemaExactness
{
    public static bool IsExact(SchemaNode node) => IsExact(node, new(ReferenceEqualityComparer.Instance));

    private static bool IsExact(SchemaNode node, HashSet<object> visited)
    {
        if (!visited.Add(node))
        {
            return true;
        }

        SchemaBehavior behavior = node.Behavior;

        bool exact = behavior.Normalizers.IsEmpty
            && behavior.Refinements.IsEmpty
            && behavior.Coercion == SchemaCoercion.None
            && behavior.NumberCheck is null
            && !behavior.IgnoresNameCase
            && !(behavior.FormatIsFluent && node.Format is not null && StringFormats.IsKnown(node.Format));

        return exact
            && node.Children.All(child => IsExact(child, visited))
            && (behavior.Target is not { } target || IsExact(target(), visited));
    }
}
