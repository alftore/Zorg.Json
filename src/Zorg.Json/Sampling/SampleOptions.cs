namespace Zorg.Json.Sampling;

/// <summary>
/// Options for <see cref="SampleExtensions.GenerateSample"/>. NJsonSchema equivalent: <c>SampleJsonDataGeneratorSettings</c>.
/// </summary>
/// <example>
/// <code>
/// SampleResult sample = schema.GenerateSample(new SampleOptions { IncludeOptionalProperties = false });
/// </code>
/// </example>
public sealed record SampleOptions
{
    /// <summary>
    /// Whether properties outside <c>required</c> are filled in. Default: <see langword="true"/>, as in NJsonSchema,
    /// where <c>GenerateOptionalProperties</c> is the equivalent.
    /// </summary>
    /// <example>
    /// <code>
    /// SampleOptions options = new() { IncludeOptionalProperties = false };
    /// </code>
    /// </example>
    public bool IncludeOptionalProperties { get; init; } = true;

    /// <summary>
    /// How many times a reference may repeat on the way down before recursion stops, by omitting optional properties
    /// and leaving arrays at <c>minItems</c>. Default: 3. A negative value counts as 0. NJsonSchema equivalent:
    /// <c>MaxRecursionLevel</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// SampleOptions options = new() { MaxRecursionDepth = 1 };
    /// </code>
    /// </example>
    public int MaxRecursionDepth { get; init; } = 3;

    /// <summary>
    /// How many items an array gets, bounded by <c>minItems</c> and <c>maxItems</c>. Default: 1. A negative value counts
    /// as 0. NJsonSchema has no equivalent: it always writes one item.
    /// </summary>
    /// <example>
    /// <code>
    /// SampleOptions options = new() { ArrayItemCount = 3 };
    /// </code>
    /// </example>
    public int ArrayItemCount { get; init; } = 1;
}
