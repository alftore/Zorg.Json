using System;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Zorg.Json.Validation;

internal static class Arguments
{
    /// <summary>
    /// Keywords of JSON Schema and of the OpenAPI 3.0 schema object. The emitter writes these itself, so a custom keyword with
    /// one of these names would replace what it wrote and could make the schema reject what parsing accepts.
    /// </summary>
    private static readonly FrozenSet<string> ReservedKeywords = new[]
    {
        "$schema", "$id", "$ref", "$anchor", "$dynamicRef", "$dynamicAnchor", "$vocabulary", "$comment", "$defs", "definitions", "title",
        "description", "default", "deprecated", "readOnly", "writeOnly", "examples", "example", "nullable", "discriminator", "xml",
        "externalDocs", "type", "const", "enum", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf", "minLength",
        "maxLength", "pattern", "format", "contentEncoding", "contentMediaType", "contentSchema", "items", "prefixItems", "additionalItems",
        "contains", "minContains", "maxContains", "minItems", "maxItems", "uniqueItems", "unevaluatedItems", "properties", "required",
        "patternProperties", "additionalProperties", "propertyNames", "minProperties", "maxProperties", "dependentRequired",
        "dependentSchemas", "dependencies", "unevaluatedProperties", "allOf", "anyOf", "oneOf", "not", "if", "then", "else",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Rejects a keyword that the emitter writes from the schema's own rules.</summary>
    public static void CustomKeyword(string keyword, [CallerArgumentExpression(nameof(keyword))] string? parameter = null)
    {
        if (ReservedKeywords.Contains(keyword))
        {
            throw new ArgumentException($"'{keyword}' is a JSON Schema keyword, and the emitter writes it from the schema's own rules. Use the method for it, or a name that starts with x-.", parameter);
        }
    }

    /// <summary>
    /// Rejects text with an unpaired surrogate. JSON text is UTF-8 and the emitted schema is serialized as UTF-8, so such
    /// a name or value would be replaced or fail later, far from the mistake.
    /// </summary>
    public static void WellFormed(string value, [CallerArgumentExpression(nameof(value))] string? parameter = null)
    {
        if (JsonText.HasLoneSurrogate(value))
        {
            throw new ArgumentException("The text has an unpaired surrogate, which can't be written as JSON.", parameter);
        }
    }

    /// <summary>Rejects a JSON value that holds a string with an unpaired surrogate escape, such as <c>"\ud800"</c>.</summary>
    public static void WellFormed(JsonElement value, [CallerArgumentExpression(nameof(value))] string? parameter = null)
    {
        if (JsonText.HasUndecodableString(JsonMarshal.GetRawUtf8Value(value)))
        {
            throw new ArgumentException("The value has a string with an unpaired surrogate, which can't be written as JSON.", parameter);
        }
    }
}
