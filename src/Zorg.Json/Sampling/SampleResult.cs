using System.Collections.Immutable;
using System.Text.Json;

namespace Zorg.Json.Sampling;

/// <summary>
/// The outcome of <see cref="SampleExtensions.GenerateSample"/>. NJsonSchema equivalent: the <c>JToken</c> returned by
/// <c>SampleJsonDataGenerator.Generate</c>, which isn't checked against the schema.
/// </summary>
/// <example>
/// <code>
/// SampleResult sample = schema.GenerateSample();
/// if (!sample.IsValid)
/// {
///     Console.WriteLine(sample.Issues.ToPrettyString());
/// }
/// </code>
/// </example>
public sealed class SampleResult
{
    internal SampleResult(JsonElement value, ImmutableArray<ParseIssue> issues)
    {
        Value = value;
        Issues = issues;
    }

    /// <summary>
    /// The document. When <see cref="IsValid"/> is false, it's the closest attempt, or <c>null</c> if there was none.
    /// NJsonSchema equivalent: the returned <c>JToken</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// string json = schema.GenerateSample().Value.GetRawText();
    /// </code>
    /// </example>
    public JsonElement Value { get; }

    /// <summary>
    /// Whether <see cref="Value"/> passed the schema. False doesn't prove that the schema is unsatisfiable, only that no
    /// conforming document was found. It's also false for a schema with <c>RefineAsync</c>, which can't run here.
    /// NJsonSchema has no equivalent.
    /// </summary>
    /// <example>
    /// <code>
    /// bool satisfiable = schema.GenerateSample().IsValid;
    /// </code>
    /// </example>
    public bool IsValid => Issues.IsEmpty;

    /// <summary>Why <see cref="Value"/> doesn't conform. Empty when <see cref="IsValid"/> is true. NJsonSchema has no equivalent.</summary>
    /// <example>
    /// <code>
    /// ImmutableArray&lt;ParseIssue&gt; issues = schema.GenerateSample().Issues;
    /// </code>
    /// </example>
    public ImmutableArray<ParseIssue> Issues { get; }
}
