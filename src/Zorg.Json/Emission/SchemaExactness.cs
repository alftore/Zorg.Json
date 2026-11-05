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
    public static bool IsExact(SchemaNode node, JsonSchemaDialect dialect) => IsExact(node, dialect, new(ReferenceEqualityComparer.Instance));

    private static bool IsExact(SchemaNode node, JsonSchemaDialect dialect, HashSet<object> visited)
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
            && behavior.PatternRegex is null
            && !behavior.IgnoresNameCase
            && !(behavior.FormatIsFluent && node.Format is not null && StringFormats.IsKnown(node.Format))
            && !CountsChange(node)
            && !HasUnwritableValue(node)
            && !LeavesOut(node, dialect);

        return exact
            && node.Children.All(child => IsExact(child, dialect, visited))
            && (behavior.Target is not { } target || IsExact(target(), dialect, visited));
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
            || node.If is not null
            || node.Then is not null
            || node.Else is not null
            || node.UnevaluatedItems is not null
            || node.UnevaluatedProperties is not null,

        _ => false,
    };
}
