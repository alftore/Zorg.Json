namespace Zorg.Json.Import;

/// <summary>The stable values of <see cref="JsonSchemaImportException.Code"/>.</summary>
/// <example>
/// <code>
/// catch (JsonSchemaImportException exception) when (exception.Code == JsonSchemaImportCodes.RefCycle)
/// {
///     logger.LogWarning("{Location}: {Message}", exception.SchemaLocation, exception.Message);
/// }
/// </code>
/// </example>
public static class JsonSchemaImportCodes
{
    /// <summary>
    /// A known keyword that Zorg can't honour, such as <c>$dynamicRef</c> beside <c>$ref</c>.
    /// Importing it anyway would silently loosen the schema.
    /// </summary>
    public const string UnsupportedKeyword = "unsupported_keyword";

    /// <summary>
    /// A <c>$schema</c> that isn't Draft 2020-12, 2019-09, draft-07, draft-06 or draft-04. Also a boolean <c>nullable</c> in a document that has no
    /// <c>$schema</c> while <see cref="JsonSchemaImportOptions.DefaultDialect"/> is left alone, which would be ignored.
    /// </summary>
    public const string UnsupportedDialect = "unsupported_dialect";

    /// <summary>A <c>pattern</c> or <c>patternProperties</c> key that isn't a valid regular expression.</summary>
    public const string InvalidPattern = "invalid_pattern";

    /// <summary>The document isn't a valid schema: malformed JSON, a keyword with a value of the wrong shape, or text with an unpaired surrogate.</summary>
    public const string InvalidSchema = "invalid_schema";

    /// <summary>A <c>$ref</c> cycle that consumes no input, so that evaluating it would never end.</summary>
    public const string RefCycle = "ref_cycle";

    /// <summary>A <c>$ref</c> that isn't local, or that points at nothing. Nothing is ever fetched.</summary>
    public const string UnresolvedRef = "unresolved_ref";

    /// <summary>The document exceeds a limit in <see cref="JsonSchemaImportOptions"/>.</summary>
    public const string LimitExceeded = "limit_exceeded";
}
