namespace Zorg.Json.Import;

/// <summary>
/// Options for <see cref="Z.FromJsonSchema(System.Text.Json.JsonElement, JsonSchemaImportOptions?)"/>.
/// Zod equivalent: the options of <c>z.fromJSONSchema</c>.
/// </summary>
/// <example>
/// <code>
/// JsonValueSchema schema = Z.FromJsonSchema(text, new JsonSchemaImportOptions
/// {
///     DefaultDialect = JsonSchemaImportDialect.Draft07,
///     MaxDepth = 32,
/// });
/// </code>
/// </example>
public sealed record JsonSchemaImportOptions
{
    /// <summary>The dialect of a document with no <c>$schema</c> keyword. Default: <see cref="JsonSchemaImportDialect.Draft202012"/>.</summary>
    public JsonSchemaImportDialect DefaultDialect { get; init; } = JsonSchemaImportDialect.Draft202012;

    /// <summary>What to do with keywords no dialect defines. Default: <see cref="UnknownKeywordHandling.Preserve"/>.</summary>
    public UnknownKeywordHandling UnknownKeywords { get; init; } = UnknownKeywordHandling.Preserve;

    /// <summary>Whether the <c>format</c> keyword is checked. Default: <see cref="FormatHandling.AssertKnown"/>.</summary>
    public FormatHandling Formats { get; init; } = FormatHandling.AssertKnown;

    /// <summary>What to do with a <c>format</c> Zorg doesn't know. Default: <see cref="UnknownFormatHandling.Annotate"/>.</summary>
    public UnknownFormatHandling UnknownFormats { get; init; } = UnknownFormatHandling.Annotate;

    /// <summary>
    /// How deeply subschemas may nest, and how long a chain of <c>$ref</c> and in-place applicators may be. Exceeding it
    /// fails with <see cref="JsonSchemaImportCodes.LimitExceeded"/>. Default: 64.
    /// </summary>
    public int MaxDepth { get; init; } = 64;

    /// <summary>
    /// How many subschemas a document may have. A <c>$ref</c> target counts once, however often it's used.
    /// Exceeding it fails with <see cref="JsonSchemaImportCodes.LimitExceeded"/>. Default: 10,000.
    /// </summary>
    public int MaxNodes { get; init; } = 10_000;

    /// <summary>
    /// The longest <c>pattern</c> or <c>patternProperties</c> key, in UTF-16 units.
    /// Exceeding it fails with <see cref="JsonSchemaImportCodes.LimitExceeded"/>. Default: 1,024.
    /// </summary>
    public int MaxPatternLength { get; init; } = 1_024;

    /// <summary>
    /// The total length of all distinct <c>pattern</c> texts and <c>patternProperties</c> keys, in UTF-16 units. A pattern that
    /// appears more than once counts once, because it is compiled once. Exceeding it fails with
    /// <see cref="JsonSchemaImportCodes.LimitExceeded"/>. Default: 65,536.
    /// </summary>
    public int MaxTotalPatternLength { get; init; } = 65_536;
}

/// <summary>The JSON Schema dialects that can be imported, chosen by <c>$schema</c>. Zod equivalent: the dialect detection in <c>z.fromJSONSchema</c>.</summary>
/// <example>
/// <code>
/// JsonValueSchema schema = Z.FromJsonSchema(text, new() { DefaultDialect = JsonSchemaImportDialect.Draft07 });
/// </code>
/// </example>
public enum JsonSchemaImportDialect
{
    /// <summary>JSON Schema Draft 2020-12.</summary>
    Draft202012,

    /// <summary>JSON Schema Draft-07.</summary>
    Draft07,

    /// <summary>JSON Schema Draft-06.</summary>
    Draft06,

    /// <summary>JSON Schema Draft-04, as written by Zod for <c>target: "draft-04"</c>.</summary>
    Draft04,
}

/// <summary>What to do with keywords no dialect defines. Zod keeps them as metadata, as <see cref="Preserve"/> does.</summary>
/// <example>
/// <code>
/// JsonValueSchema schema = Z.FromJsonSchema(text, new() { UnknownKeywords = UnknownKeywordHandling.Error });
/// </code>
/// </example>
public enum UnknownKeywordHandling
{
    /// <summary>Keep them in <see cref="Model.SchemaNode.Extensions"/>, so that they're emitted again.</summary>
    Preserve,

    /// <summary>Fail with <see cref="JsonSchemaImportCodes.UnsupportedKeyword"/>, except for <c>x-*</c> keywords.</summary>
    Error,
}

/// <summary>Whether the <c>format</c> keyword is checked. Zod equivalent: none, because its string formats always assert.</summary>
/// <example>
/// <code>
/// JsonValueSchema schema = Z.FromJsonSchema(text, new() { Formats = FormatHandling.AnnotateOnly });
/// </code>
/// </example>
public enum FormatHandling
{
    /// <summary>Check every format Zorg knows, which is its stance for fluent schemas too.</summary>
    AssertKnown,

    /// <summary>Treat <c>format</c> as an annotation only, the default in the JSON Schema specification.</summary>
    AnnotateOnly,
}

/// <summary>What to do with a <c>format</c> Zorg doesn't know. Zod ignores custom formats, as <see cref="Annotate"/> does.</summary>
/// <example>
/// <code>
/// JsonValueSchema schema = Z.FromJsonSchema(text, new() { UnknownFormats = UnknownFormatHandling.Error });
/// </code>
/// </example>
public enum UnknownFormatHandling
{
    /// <summary>Keep it as an annotation, as the JSON Schema specification requires.</summary>
    Annotate,

    /// <summary>Fail with <see cref="JsonSchemaImportCodes.UnsupportedKeyword"/>.</summary>
    Error,
}
