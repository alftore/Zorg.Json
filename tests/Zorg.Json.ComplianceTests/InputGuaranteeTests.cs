using System.Text.Json;
using Json.Schema;
using Xunit;
using Zorg.Json.Emission;

namespace Zorg.Json.ComplianceTests;

/// <summary>Checks emitted schemas against JsonSchema.Net, an independent validator.</summary>
public sealed class InputGuaranteeTests
{
    private static readonly string[] Documents =
    [
        "\"\"", "\"a\"", "\"A\"", "\"ab\"", "\"abc\"", "\"abcd\"", "\"  a  \"", "\"  abc  \"", "\"   \"", "\"😀\"", "\"😀😀\"",
        "\"web\"", "\"WEB\"", "\" web \"", "\"ann@example.com\"", "\" ann@example.com \"", "\"2026-11-03\"",
        "1", "1.5", "true", "null", "[]", "{}",
    ];

    public static TheoryData<string, StringSchema> ExactSchemas { get; } = new()
    {
        { "plain", Z.String() },
        { "minLength", Z.String().MinLength(2) },
        { "maxLength", Z.String().MaxLength(2) },
        { "pattern", Z.String().Pattern("^a") },
        { "const", Z.String().Const("web") },
        { "enum", Z.String().Enum("a", "web") },
    };

    public static TheoryData<string, StringSchema> LooserSchemas { get; } = new()
    {
        { "email", Z.String().Email() },
        { "date", Z.String().Date() },
        { "refine", Z.String().Refine(text => text.Length > 1, "Too short") },
        { "trim, minLength", Z.String().Trim().MinLength(1) },
        { "trim, maxLength", Z.String().Trim().MaxLength(3) },
        { "trim, pattern", Z.String().Trim().Pattern("^a") },
        { "trim, email", Z.String().Trim().Email() },
        { "lower, const", Z.String().ToLowerInvariant().Const("web") },
        { "lower, enum", Z.String().ToLowerInvariant().Enum("a", "web") },
        { "overwrite, maxLength", Z.String().Overwrite(_ => "a").MaxLength(1) },
    };

    private static EvaluationResults Evaluate(StringSchema schema, string document) =>
        JsonSchema.FromText(schema.ToJsonSchema().ToJsonString()).Evaluate(JsonElement.Parse(document));

    [Theory]
    [MemberData(nameof(ExactSchemas))]
    public void Emitted_schema_agrees_with_parsing(string name, StringSchema schema)
    {
        foreach (string document in Documents)
        {
            Assert.True(
                schema.SafeParse(document).IsSuccess == Evaluate(schema, document).IsValid,
                $"{name} disagrees on {document}");
        }
    }

    [Theory]
    [MemberData(nameof(LooserSchemas))]
    public void Emitted_schema_is_never_stricter_than_parsing(string name, StringSchema schema)
    {
        foreach (string document in Documents)
        {
            if (schema.SafeParse(document).IsSuccess)
            {
                Assert.True(Evaluate(schema, document).IsValid, $"{name} is stricter than parsing on {document}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(ExactSchemas))]
    [MemberData(nameof(LooserSchemas))]
    public void Emitted_schema_is_valid_against_the_meta_schema(string name, StringSchema schema)
    {
        JsonElement draft202012 = JsonElement.Parse(schema.ToJsonSchema().ToJsonString());
        JsonElement draft07 = JsonElement.Parse(schema.ToJsonSchema(new() { Target = JsonSchemaProfile.Draft07 }).ToJsonString());

        Assert.True(MetaSchemas.Draft202012.Evaluate(draft202012).IsValid, name);
        Assert.True(MetaSchemas.Draft7.Evaluate(draft07).IsValid, name);
    }
}
