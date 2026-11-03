using System.Collections.Immutable;
using System.Linq;

namespace Zorg.Json.Llm;

/// <summary>The outcome of <see cref="LlmSchemaExtensions.CheckCompatibility"/>.</summary>
/// <param name="ProfileName">The profile that was checked.</param>
/// <param name="Capabilities">The table that was used.</param>
/// <param name="Issues">The findings, with errors first.</param>
/// <example>
/// <code>
/// CompatibilityReport report = schema.CheckCompatibility(LlmProfiles.OpenAIStrict);
/// foreach (CompatibilityIssue issue in report.Issues)
/// {
///     Console.WriteLine($"{issue.Severity} {issue.Location}: {issue.Message}");
/// }
/// </code>
/// </example>
public sealed record CompatibilityReport(string ProfileName, SchemaCapabilities Capabilities, ImmutableArray<CompatibilityIssue> Issues)
{
    /// <summary>Whether the provider can use the schema, meaning no issue is a <see cref="CompatibilitySeverity.Error"/>.</summary>
    public bool IsCompatible => Issues.All(issue => issue.Severity != CompatibilitySeverity.Error);
}

/// <summary>One finding about a schema and a provider.</summary>
/// <param name="Severity">How much it matters.</param>
/// <param name="Kind">What was found.</param>
/// <param name="Keyword">The JSON Schema keyword, when there is one.</param>
/// <param name="Message">An explanation.</param>
/// <param name="Location">A JSON Pointer into the full schema or, for limits and residual keywords, the emitted one. Empty for the root.</param>
public sealed record CompatibilityIssue(CompatibilitySeverity Severity, CompatibilityIssueKind Kind, string? Keyword, string Message, string Location = "");

/// <summary>How much a finding matters.</summary>
public enum CompatibilitySeverity
{
    /// <summary>The provider will reject the schema, or the model can't produce a useful answer.</summary>
    Error,

    /// <summary>The provider won't enforce a rule. Only <c>ParseModelOutput</c> will, so an answer can fail there.</summary>
    Warning,

    /// <summary>The emitted schema differs in a way that <c>ParseModelOutput</c> undoes or that has no effect on validity.</summary>
    Info,
}

/// <summary>What a finding is about.</summary>
public enum CompatibilityIssueKind
{
    /// <summary>A keyword the provider doesn't accept is still in the emitted schema.</summary>
    UnsupportedKeyword,

    /// <summary>A keyword moved to <c>description</c> or to a looser form.</summary>
    Relocated,

    /// <summary>The root isn't <c>type: object</c>, or it was wrapped to make it one.</summary>
    Root,

    /// <summary>An object isn't closed, or a map's value schema can't be stated.</summary>
    OpenObject,

    /// <summary>A property isn't in <c>required</c>, or it was made required and nullable.</summary>
    OptionalProperty,

    /// <summary>A subschema doesn't say what it is.</summary>
    MissingType,

    /// <summary>A <c>$ref</c> leads back to itself.</summary>
    Recursion,

    /// <summary>A <c>pattern</c> may be too complex for the provider.</summary>
    RegexComplexity,

    /// <summary>A C# refinement can't reach the provider.</summary>
    Refinement,

    /// <summary>A documented count or size is exceeded.</summary>
    LimitExceeded,
}
