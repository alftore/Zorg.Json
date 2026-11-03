namespace Zorg.Json;

/// <summary>The stable values of <see cref="ParseIssue.Code"/>. Zod's names are used where they exist.</summary>
public static class IssueCodes
{
    /// <summary>The input isn't well-formed JSON, or it has a duplicate property.</summary>
    public const string InvalidJson = "invalid_json";

    /// <summary>The value has the wrong JSON type. Keyword: <c>type</c>.</summary>
    public const string InvalidType = "invalid_type";

    /// <summary>The value is below a minimum. Keyword: <c>minLength</c>.</summary>
    public const string TooSmall = "too_small";

    /// <summary>The value is above a maximum. Keyword: <c>maxLength</c>.</summary>
    public const string TooBig = "too_big";

    /// <summary>The value doesn't match a format. Keywords: <c>pattern</c>, <c>format</c>.</summary>
    public const string InvalidFormat = "invalid_format";

    /// <summary>The value isn't one of the allowed values. Keywords: <c>const</c>, <c>enum</c>.</summary>
    public const string InvalidValue = "invalid_value";

    /// <summary>A refinement failed. Its message is the one given to <c>Refine</c>.</summary>
    public const string Custom = "custom";
}
