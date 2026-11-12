using System;
using System.Text;
using System.Text.Json;
using Json.Schema;
using Xunit;
using Zorg.Json.Emission;
using Zorg.Json.Import;

namespace Zorg.Json.ComplianceTests;

/// <summary><c>unevaluatedProperties</c> and <c>unevaluatedItems</c> beside other subschemas, beyond what the official suite covers.</summary>
public sealed class UnevaluatedTests
{
    private static JsonValueSchema Asserting(string document) => Z.FromJsonSchema(document, new JsonSchemaImportOptions { Formats = FormatHandling.AssertKnown });

    [Theory]
    [InlineData("""{"if":{"format":"email"},"then":{"properties":{"a":{}}},"unevaluatedProperties":false}""", """{"a":1}""")]
    [InlineData("""{"allOf":[{"if":{"format":"email"},"then":{"properties":{"a":{}}}}],"unevaluatedProperties":false}""", """{"a":1}""")]
    [InlineData("""{"$defs":{"c":{"if":{"format":"email"},"else":{},"then":{"properties":{"a":{}}}}},"$ref":"#/$defs/c","unevaluatedProperties":false}""", """{"a":1}""")]
    [InlineData("""{"if":{"format":"email"},"then":{"prefixItems":[{}]},"unevaluatedItems":false}""", "[1]")]
    public void A_left_out_if_takes_unevaluated_with_it(string document, string instance)
    {
        JsonValueSchema schema = Asserting(document);
        string text = schema.ToJsonSchema().ToJsonString();

        Assert.True(schema.SafeParse(instance).IsSuccess);
        Assert.DoesNotContain("\"unevaluated", text, StringComparison.Ordinal);
        Assert.True(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft202012, instance), text);
    }

    [Fact]
    public void An_exact_if_keeps_unevaluated()
    {
        const string Document = """{"if":{"required":["b"]},"then":{"properties":{"a":{}}},"unevaluatedProperties":false}""";

        Assert.Contains("unevaluatedProperties", Z.FromJsonSchema(Document).ToJsonSchema().ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Issues_name_the_keyword_and_the_properties()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""{"allOf":[{"properties":{"a":{}}}],"unevaluatedProperties":false}""");

        ParseIssue issue = Assert.Single(schema.SafeParse("""{"a":1,"b":2}""").Issues);

        Assert.Equal(IssueCodes.UnrecognizedKeys, issue.Code);
        Assert.Contains("b", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_that_failed_is_not_searched()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""{"allOf":[{"properties":{"a":{"type":"string"}}}],"unevaluatedProperties":false}""");

        Assert.Equal(IssueCodes.InvalidType, Assert.Single(schema.SafeParse("""{"a":1}""").Issues).Code);
    }

    [Fact]
    public void A_name_that_can_not_be_read_fails_the_parse_without_throwing()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""{"allOf":[{}],"unevaluatedProperties":false}""");

        Assert.False(schema.SafeParse("""{"\ud800":1}""").IsSuccess);
    }

    [Fact]
    public void Nested_unions_end_within_the_limits()
    {
        string document = """{"properties":{"z":{}}}""";

        for (int depth = 0; depth < 24; depth++)
        {
            document = """{"anyOf":[""" + document + "," + document + """],"unevaluatedProperties":{"type":"string"}}""";

            if (document.Length > 200_000)
            {
                document = """{"$defs":{"d":""" + document + """},"anyOf":[{"$ref":"#/$defs/d"},{"$ref":"#/$defs/d"}],"unevaluatedProperties":{"type":"string"}}""";
                break;
            }
        }

        JsonValueSchema schema = Z.FromJsonSchema(document, new JsonSchemaImportOptions { MaxNodes = 1_000_000 });
        Timing.Within(TimeSpan.FromSeconds(20), () => schema.SafeParse("""{"z":1,"a":"b"}"""));
    }

    private static readonly string[] Branches =
    [
        """{"properties":{"a":{}}}""", """{"properties":{"b":{"type":"string"}}}""", """{"patternProperties":{"^c":{}}}""", """{"required":["a"]}""", """{"additionalProperties":{"type":"integer"}}""",
        """{"unevaluatedProperties":{"type":"integer"}}""", """{"prefixItems":[{}]}""", """{"prefixItems":[{},{"type":"string"}]}""", """{"contains":{"type":"string"}}""", """{"items":{"type":"integer"}}""",
        """{"unevaluatedItems":{"type":"string"}}""", "true", "false", """{"$ref":"#/$defs/x"}""",
    ];

    private static readonly string[] Instances =
    [
        "{}", """{"a":1}""", """{"b":"x"}""", """{"b":1}""", """{"c1":1}""", """{"d":1}""", """{"d":"x"}""", """{"a":1,"b":"x","d":2}""", """{"a":1,"c":true}""",
        "[]", "[1]", """["x"]""", """[1,"x"]""", """[1,2]""", """["x",1,"y"]""", """[1,"x",true]""", "1", "\"s\"",
    ];

    private static string Pick(Random random, int depth)
    {
        if (depth > 2 || random.Next(3) == 0)
        {
            return Branches[random.Next(Branches.Length)];
        }

        string Sub() => Pick(random, depth + 1);

        return random.Next(7) switch
        {
            0 => $$"""{"allOf":[{{Sub()}},{{Sub()}}]}""",
            1 => $$"""{"anyOf":[{{Sub()}},{{Sub()}}]}""",
            2 => $$"""{"oneOf":[{{Sub()}},{{Sub()}}]}""",
            3 => $$"""{"if":{{Sub()}},"then":{{Sub()}},"else":{{Sub()}}}""",
            4 => """{"dependentSchemas":{"a":""" + Sub() + "}}",
            5 => $$"""{"not":{{Sub()}}}""",
            _ => $$"""{"allOf":[{{Sub()}}],"unevaluatedProperties":{{(random.Next(2) == 0 ? "false" : """{"type":"string"}""")}},"unevaluatedItems":{{(random.Next(2) == 0 ? "false" : """{"type":"integer"}""")}}}""",
        };
    }

    /// <summary>Random nestings, judged by JsonSchema.Net, and by jv when it's there. Both ways, because this is about meaning.</summary>
    [Fact]
    public void Random_nestings_mean_what_the_specification_says()
    {
        Random random = new(20261106);
        using GoValidator? go = GoValidator.Executable is null ? null : new GoValidator();
        StringBuilder wrong = new();
        int compared = 0;

        for (int round = 0; round < 1_500; round++)
        {
            string body = Pick(random, 0);
            string document = """{"$schema":"https://json-schema.org/draft/2020-12/schema","$defs":{"x":{"properties":{"x":{}},"prefixItems":[true]}},"allOf":[""" + body
                + (random.Next(2) == 0 ? """],"unevaluatedProperties":false""" : """],"unevaluatedItems":false""") + "}";
            JsonValueSchema schema = Z.FromJsonSchema(document);
            JsonSchema oracle = JsonSchema.FromText(document, new BuildOptions { SchemaRegistry = new SchemaRegistry() });
            bool?[]? second = go?.Judge(document, Instances, "2020", out _);

            for (int index = 0; index < Instances.Length; index++)
            {
                bool parsed = schema.SafeParse(Instances[index]).IsSuccess;
                bool first = oracle.Evaluate(JsonElement.Parse(Instances[index])).IsValid;
                compared++;

                if (parsed != first || (second?[index] is { } verdict && verdict != parsed))
                {
                    wrong.Append(System.Globalization.CultureInfo.InvariantCulture, $"zorg={parsed} jsonschema.net={first} jv={second?[index]} on {Instances[index]}\n{document}\n");
                }
            }
        }

        Assert.True(wrong.Length == 0, wrong.ToString(0, Math.Min(wrong.Length, 3_000)));
        Assert.True(compared > 20_000);
    }
}
