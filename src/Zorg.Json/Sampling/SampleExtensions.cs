using System;

namespace Zorg.Json.Sampling;

/// <summary>Generates sample data from a schema. NJsonSchema equivalent: <c>SampleJsonDataGenerator</c>.</summary>
/// <example>
/// <code>
/// bool satisfiable = Z.String().MinLength(2).MaxLength(1).GenerateSample().IsValid;   // false
/// </code>
/// </example>
public static class SampleExtensions
{
    /// <summary>
    /// Generates a JSON document that conforms to the schema, and validates it. Works for fluent, typed and imported
    /// schemas, and doubles as a check that a schema is satisfiable. The output depends only on the schema and the
    /// options. NJsonSchema equivalent: <c>new SampleJsonDataGenerator().Generate(schema)</c>.
    /// </summary>
    /// <param name="schema">The schema.</param>
    /// <param name="options">What to include. Null uses the defaults.</param>
    /// <returns>The document and the verdict. If no conforming document was found, the closest attempt and its issues.</returns>
    /// <remarks>Refinements run on each candidate, and exceptions they throw pass through, as with <c>SafeParse</c>.</remarks>
    /// <example>
    /// <code>
    /// ObjectSchema schema = Z.Object()
    ///     .Property("name", Z.String().MinLength(1))
    ///     .Property("age", Z.Integer().Minimum(18));
    ///
    /// SampleResult sample = schema.GenerateSample();
    /// Console.WriteLine(sample.Value);   // {"name":"string","age":18}
    /// </code>
    /// </example>
    public static SampleResult GenerateSample(this ISchema schema, SampleOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return new SampleGenerator(schema, options ?? new SampleOptions()).Run();
    }
}
