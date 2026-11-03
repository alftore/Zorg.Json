namespace Zorg.Json.Import;

/// <summary>The stable values of <see cref="JsonSchemaImportException.Code"/>.</summary>
public static class JsonSchemaImportCodes
{
    /// <summary>
    /// A known keyword that Zorg can't honour, such as <c>$dynamicRef</c>, or <c>unevaluatedProperties</c> beside an
    /// in-place applicator. Importing it anyway would silently loosen the schema.
    /// </summary>
    public const string UnsupportedKeyword = "unsupported_keyword";

    /// <summary>A <c>$schema</c> that isn't Draft 2020-12, draft-07 or draft-06.</summary>
    public const string UnsupportedDialect = "unsupported_dialect";

    /// <summary>A <c>pattern</c> or <c>patternProperties</c> key that isn't a valid regular expression.</summary>
    public const string InvalidPattern = "invalid_pattern";

    /// <summary>The document isn't a valid schema: malformed JSON, or a keyword with a value of the wrong shape.</summary>
    public const string InvalidSchema = "invalid_schema";

    /// <summary>A <c>$ref</c> cycle that consumes no input, so that evaluating it would never end.</summary>
    public const string RefCycle = "ref_cycle";

    /// <summary>A <c>$ref</c> that isn't local, or that points at nothing. Nothing is ever fetched.</summary>
    public const string UnresolvedRef = "unresolved_ref";

    /// <summary>The document exceeds a limit in <see cref="JsonSchemaImportOptions"/>.</summary>
    public const string LimitExceeded = "limit_exceeded";
}
