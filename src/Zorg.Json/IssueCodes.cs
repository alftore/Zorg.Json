namespace Zorg.Json;

/// <summary>The stable values of <see cref="ParseIssue.Code"/>. Zod's names are used where they exist.</summary>
public static class IssueCodes
{
    /// <summary>The input isn't well-formed JSON, or it has a duplicate property.</summary>
    public const string InvalidJson = "invalid_json";

    /// <summary>The value has the wrong JSON type, or doesn't fit the numeric type. Keyword: <c>type</c>.</summary>
    public const string InvalidType = "invalid_type";

    /// <summary>
    /// The value is below a minimum. Keywords: <c>minLength</c>, <c>minimum</c>, <c>exclusiveMinimum</c>,
    /// <c>minItems</c>, <c>minProperties</c>, <c>minContains</c>.
    /// </summary>
    public const string TooSmall = "too_small";

    /// <summary>
    /// The value is above a maximum. Keywords: <c>maxLength</c>, <c>maximum</c>, <c>exclusiveMaximum</c>,
    /// <c>maxItems</c>, <c>maxProperties</c>, <c>maxContains</c>.
    /// </summary>
    public const string TooBig = "too_big";

    /// <summary>The value doesn't match a format. Keywords: <c>pattern</c>, <c>format</c>.</summary>
    public const string InvalidFormat = "invalid_format";

    /// <summary>The value isn't one of the allowed values. Keywords: <c>const</c>, <c>enum</c>, <c>not</c>, <c>uniqueItems</c>.</summary>
    public const string InvalidValue = "invalid_value";

    /// <summary>The number isn't a multiple of the divisor. Keyword: <c>multipleOf</c>.</summary>
    public const string NotMultipleOf = "not_multiple_of";

    /// <summary>A required property is missing. Keywords: <c>required</c>, <c>dependentRequired</c>.</summary>
    public const string Required = "required";

    /// <summary>The object has properties the schema doesn't allow. Keyword: <c>additionalProperties</c>.</summary>
    public const string UnrecognizedKeys = "unrecognized_keys";

    /// <summary>No branch of a union matched, or more than one of a <c>oneOf</c>. Keywords: <c>anyOf</c>, <c>oneOf</c>.</summary>
    public const string InvalidUnion = "invalid_union";

    /// <summary>The value passed validation, but System.Text.Json couldn't deserialize it.</summary>
    public const string DeserializationFailed = "deserialization_failed";

    /// <summary>A refinement failed. Its message is the one given to <c>Refine</c>.</summary>
    public const string Custom = "custom";

    /// <summary>
    /// Evaluation stopped after the first <c>100</c> issues, so the list is not complete. It is the last issue, and the
    /// parse stays failed. See the evaluation limits in the guide.
    /// </summary>
    public const string TooManyIssues = "too_many_issues";
}
