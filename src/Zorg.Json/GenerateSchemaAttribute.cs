using System;

namespace Zorg.Json;

/// <summary>
/// Generates a static <c>Schema</c> property on a partial type, from its System.Text.Json contract and its validation attributes, such as
/// <c>[MinLength]</c>, <c>[Range]</c>, <c>[RegularExpression]</c>, <c>[EmailAddress]</c> and <c>[Description]</c>. What is generated is the
/// same fluent code you could write, it needs no reflection, and it works under Native AOT. Zod equivalent: none.
/// </summary>
/// <example>
/// <code>
/// [GenerateSchema(typeof(AppJson))]
/// public partial record Person([property: MinLength(1)] string Name, [property: Range(0, 150)] int Age);
///
/// [JsonSerializable(typeof(Person))]
/// public partial class AppJson : JsonSerializerContext;
///
/// ParseResult&lt;Person&gt; person = Person.Schema.SafeParse(json);
/// </code>
/// </example>
/// <param name="jsonContext">The <c>JsonSerializerContext</c> that has a <c>[JsonSerializable]</c> for this type.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class GenerateSchemaAttribute(Type jsonContext) : Attribute
{
    /// <summary>The <c>JsonSerializerContext</c> that holds the contract.</summary>
    public Type JsonContext { get; } = jsonContext;
}
