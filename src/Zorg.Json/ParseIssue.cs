using System.Collections.Immutable;

namespace Zorg.Json;

/// <summary>One reason why a JSON document failed validation.</summary>
public sealed record ParseIssue
{
    /// <summary>The stable, machine-readable reason. See <see cref="IssueCodes"/>.</summary>
    public required string Code { get; init; }

    /// <summary>The message for people, localized from <see cref="Code"/>.</summary>
    public required string Message { get; init; }

    /// <summary>A JSON Pointer to the failing value. Empty for the document root.</summary>
    public required string InstanceLocation { get; init; }

    /// <summary>
    /// A JSON Pointer of the keywords walked to reach the failing keyword, including through <c>$ref</c>, such as
    /// <c>/properties/a/$ref/minLength</c>. JSON Schema 2020-12 output format: <c>keywordLocation</c>. Empty for <c>invalid_json</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// ParseResult&lt;JsonElement&gt; result = Z.Object().Property("a", Z.String().MinLength(3)).SafeParse("{\"a\":\"x\"}");
    /// string path = result.Issues[0].EvaluationPath; // "/properties/a/minLength"
    /// </code>
    /// </example>
    public string EvaluationPath { get; init; } = string.Empty;

    /// <summary>
    /// Where the failing keyword is in the schema document, as a URI fragment such as <c>#/$defs/a/minLength</c>. For a
    /// <c>$ref</c> target that is where the target lives, not the path walked to it. JSON Schema 2020-12 output format:
    /// <c>absoluteKeywordLocation</c>, without the base URI. Empty for <c>invalid_json</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// ParseResult&lt;JsonElement&gt; result = Z.Object().Property("a", Z.String().MinLength(3)).SafeParse("{\"a\":\"x\"}");
    /// string location = result.Issues[0].SchemaLocation; // "#/properties/a/minLength"
    /// </code>
    /// </example>
    public string SchemaLocation { get; init; } = string.Empty;

    /// <summary>The JSON Schema keyword that failed, such as <c>minLength</c>. Null when no keyword applies.</summary>
    public string? Keyword { get; init; }

    /// <summary>
    /// Details specific to <see cref="Code"/>, such as <c>minimum</c> or <c>expected</c>. <c>detail</c> holds the
    /// exception text of <c>invalid_json</c> and <c>deserialization_failed</c>, which is for logs and not for clients.
    /// </summary>
    public ImmutableDictionary<string, object?> Parameters { get; init; } = ImmutableDictionary<string, object?>.Empty;
}
