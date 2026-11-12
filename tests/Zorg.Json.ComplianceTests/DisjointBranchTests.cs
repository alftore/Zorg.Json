using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Json.Schema;
using Xunit;
using Zorg.Json.Emission;

namespace Zorg.Json.ComplianceTests;

/// <summary>
/// A <c>oneOf</c> of object branches keeps its keyword when the branches exclude each other by what they require, and only then.
/// JsonSchema.Net doesn't assert <c>format</c>, so it plays the validator that a kept <c>oneOf</c> must not trip.
/// </summary>
public sealed class DisjointBranchTests
{
    private const string Uuid = "\"2eb8aa08-aa98-11ea-b4aa-73b441d16380\"";

    private const string IdBranch = """{"type":"object","additionalProperties":false,"properties":{"id":{"type":"string","format":"uuid"}},"required":["id"]}""";

    private const string RuleBranch = """{"type":"object","additionalProperties":false,"properties":{"rule":{"type":"string"}},"required":["rule"]}""";

    private static readonly JsonSchemaProfile[] Profiles =
        [JsonSchemaProfile.Draft202012, JsonSchemaProfile.Draft07, JsonSchemaProfile.OpenApi31, JsonSchemaProfile.OpenApi30];

    private static JsonValueSchema Import(string document) =>
        Z.FromJsonSchema(document, new Zorg.Json.Import.JsonSchemaImportOptions { Formats = Zorg.Json.Import.FormatHandling.AssertKnown });

    private static string Union(params string[] branches) =>
        """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"p":{"oneOf":[""" + string.Join(",", branches) + "]}}}";

    private static string Kind(string value, string more = "") =>
        $$"""{"type":"object","properties":{"kind":{"const":{{value}}}{{more}}},"required":["kind"]}""";

    private const string WithId = ""","id":{"type":"string","format":"uuid"}""";

    public static TheoryData<string> Exclusive { get; } = new()
    {
        // The cases of the report: S, T and V.
        Union(IdBranch, RuleBranch),
        Union(IdBranch, RuleBranch, """{"type":"null"}"""),
        Union(Kind("\"a\"", WithId), Kind("\"b\"")),

        // Other ways of saying the same.
        Union(Kind("1", WithId), Kind("2")),
        Union(Kind("true", WithId), Kind("false")),
        Union(Kind("null", WithId), Kind("\"b\"")),
        Union(Kind("\"1\"", WithId), Kind("1")),
        Union("""{"type":"object","properties":{"kind":{"enum":["a","b"]},"id":{"format":"uuid"}},"required":["kind"]}""", """{"type":"object","properties":{"kind":{"enum":["c","d"]}},"required":["kind"]}"""),
        Union("""{"type":"object","properties":{"a":{"format":"uuid"},"b":false},"required":["a"]}""", """{"type":"object","properties":{"b":{"type":"string"}},"required":["b"]}"""),

        // Required in one branch only: the other still limits the value when it's there.
        Union(Kind("\"a\"", WithId), """{"type":"object","properties":{"kind":{"const":"b"}}}"""),
    };

    public static TheoryData<string> Exclusive2020Only { get; } = new()
    {
        // Behind references, as in the report's real schema.
        """{"$schema":"https://json-schema.org/draft/2020-12/schema","$defs":{"id":""" + IdBranch + ""","rule":""" + RuleBranch
            + """},"type":"object","properties":{"p":{"oneOf":[{"$ref":"#/$defs/id"},{"$ref":"#/$defs/rule"},{"type":"null"}]}}}""",
        """
        {"$schema":"https://json-schema.org/draft/2020-12/schema",
         "$defs":{"a":{"const":"a"},"none":false},
         "type":"object","properties":{"p":{"oneOf":[
           {"type":"object","properties":{"kind":{"$ref":"#/$defs/a"},"id":{"format":"uuid"}},"required":["kind"]},
           {"type":"object","properties":{"kind":{"$ref":"#/$defs/none"}}}]}}}
        """,
    };

    [Theory]
    [MemberData(nameof(Exclusive))]
    [MemberData(nameof(Exclusive2020Only))]
    public void Branches_that_exclude_each_other_round_trip(string document)
    {
        JsonValueSchema schema = Import(document);
        EmitResult result = schema.Emit(new JsonSchemaOptions());

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(document), result.Schema), result.Schema.ToJsonString());
        Assert.Empty(result.Diagnostics);
        Assert.NotNull(schema.Emit(new JsonSchemaOptions { Unrepresentable = UnrepresentablePolicy.Throw }));
    }

    [Theory]
    [MemberData(nameof(Exclusive))]
    [MemberData(nameof(Exclusive2020Only))]
    public void Branches_that_exclude_each_other_keep_one_of_in_every_profile(string document)
    {
        // OpenAPI 3.0 doesn't trust null in a value list: see the test for that.
        foreach (JsonSchemaProfile profile in Profiles.Where(profile => profile.Dialect != JsonSchemaDialect.OpenApi30 || !document.Contains("\"const\":null", StringComparison.Ordinal)))
        {
            string text = Import(document).ToJsonSchema(new JsonSchemaOptions { Target = profile }).ToJsonString();

            Assert.Contains("\"oneOf\"", text, StringComparison.Ordinal);
        }
    }

    public static TheoryData<string, string> Overlapping { get; } = new()
    {
        // No type: a string matches both.
        { """{"oneOf":[{"properties":{"k":{"const":"a"},"id":{"format":"uuid"}},"required":["k"]},{"properties":{"k":{"const":"b"}},"required":["k"]}]}""", "\"text\"" },
        { """{"oneOf":[{"type":"object","required":["k"],"properties":{"k":{"const":"a","format":"uuid"}}},{"required":["k"],"properties":{"k":{"const":"b"}}}]}""", """{"k":"a"}""" },

        // Null belongs to both.
        { """{"oneOf":[{"type":["object","null"],"properties":{"k":{"const":"a"},"id":{"format":"uuid"}},"required":["k"]},{"type":["object","null"],"properties":{"k":{"const":"b"}},"required":["k"]}]}""", "null" },

        // Nothing is required, so an empty object matches both.
        { """{"oneOf":[{"type":"object","properties":{"k":{"const":"a"},"id":{"format":"uuid"}}},{"type":"object","properties":{"k":{"const":"b"}}}]}""", """{"id":"x"}""" },

        // The value lists meet.
        { """{"oneOf":[{"type":"object","properties":{"k":{"const":"a"},"id":{"format":"uuid"}},"required":["k","id"]},{"type":"object","properties":{"k":{"const":"a"}},"required":["k"]}]}""", """{"k":"a","id":"x"}""" },
        { """{"oneOf":[{"type":"object","properties":{"k":{"enum":["a","b"]},"id":{"format":"uuid"}},"required":["k","id"]},{"type":"object","properties":{"k":{"enum":["b","c"]}},"required":["k"]}]}""", """{"k":"b","id":"x"}""" },
        { """{"oneOf":[{"type":"object","properties":{"k":{"const":1},"id":{"format":"uuid"}},"required":["k","id"]},{"type":"object","properties":{"k":{"const":1.0}},"required":["k"]}]}""", """{"k":1,"id":"x"}""" },

        // Numbers a double can't tell apart.
        { """{"oneOf":[{"type":"object","properties":{"k":{"const":9007199254740993},"id":{"format":"uuid"}},"required":["k","id"]},{"type":"object","properties":{"k":{"const":9007199254740992}},"required":["k"]}]}""", """{"k":9007199254740992,"id":"x"}""" },

        // Values that aren't plain aren't compared.
        { """{"oneOf":[{"type":"object","properties":{"k":{"const":[1]},"id":{"format":"uuid"}},"required":["k","id"]},{"type":"object","properties":{"k":{"const":[1.0]}},"required":["k"]}]}""", """{"k":[1],"id":"x"}""" },

        // Another keyword lets the required name through.
        { """{"oneOf":[{"type":"object","properties":{"a":{"format":"uuid"}},"required":["a"]},{"type":"object","additionalProperties":false,"patternProperties":{"^a":{}},"properties":{"b":{}}}]}""", """{"a":"x"}""" },
        { """{"oneOf":[{"type":"object","properties":{"a":{"format":"uuid"}},"required":["a"]},{"type":"object","additionalProperties":{"type":"string"},"properties":{"b":{}}}]}""", """{"a":"x"}""" },
        { """{"oneOf":[{"type":"object","properties":{"a":{"format":"uuid"}},"required":["a"]},{"type":"object","properties":{"b":{}}}]}""", """{"a":"x"}""" },
    };

    [Theory]
    [MemberData(nameof(Overlapping))]
    public void Branches_that_can_overlap_still_relax_to_any_of(string document, string instance)
    {
        JsonValueSchema schema = Import(document);

        foreach (JsonSchemaProfile profile in Profiles)
        {
            string text = schema.ToJsonSchema(new JsonSchemaOptions { Target = profile }).ToJsonString();

            Assert.DoesNotContain("\"oneOf\"", text, StringComparison.Ordinal);

            if (schema.SafeParse(instance).IsSuccess)
            {
                Assert.True(EmissionReviewTests.Accepts(text, profile, instance), text);
            }
        }
    }

    [Fact]
    public void A_normalized_value_is_not_written_so_it_excludes_nothing()
    {
        JsonValueSchema schema = Z.OneOf(
            Z.Object().Property("k", Z.String().Trim().Const("a")).Property("id", Z.String().Uuid()),
            Z.Object().Property("k", Z.String().Trim().Const("b")).Property("id", Z.String().Uuid()));

        Assert.DoesNotContain("\"oneOf\"", schema.ToJsonSchema().ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_coerced_value_is_not_written_so_it_excludes_nothing()
    {
        JsonValueSchema schema = Z.OneOf(
            Z.Object().Property("k", Z.Integer().Coerce().Const(1)).Property("id", Z.String().Uuid()),
            Z.Object().Property("k", Z.Integer().Coerce().Const(2)).Property("id", Z.String().Uuid()));

        Assert.DoesNotContain("\"oneOf\"", schema.ToJsonSchema().ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_stripping_object_is_open_in_the_input_schema()
    {
        JsonValueSchema schema = Z.OneOf(
            Z.Object().Property("a", Z.String().Uuid()).Strip(),
            Z.Object().Property("b", Z.String()).Strip());

        string text = schema.ToJsonSchema().ToJsonString();

        Assert.DoesNotContain("\"oneOf\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_closed_fluent_object_excludes_what_it_does_not_name()
    {
        JsonValueSchema schema = Z.OneOf(
            Z.Object().Strict().Property("a", Z.String().Uuid()),
            Z.Object().Strict().Property("b", Z.String()));

        Assert.Contains("\"oneOf\"", schema.ToJsonSchema().ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_json_can_not_spell_excludes_nothing()
    {
        const string Document = """{"oneOf":[{"type":"object","properties":{"k":{"const":"\ud800"},"id":{"format":"uuid"}},"required":["k"]},{"type":"object","properties":{"k":{"const":"b"}},"required":["k"]}]}""";

        Assert.DoesNotContain("\"oneOf\"", Import(Document).ToJsonSchema().ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"type":["string","null"],"enum":["a"]}""", """{"type":["string","null"],"enum":["b"]}""")]
    [InlineData("""{"enum":["a",null]}""", """{"enum":["b"]}""")]
    [InlineData("""{"const":null}""", """{"enum":["b"]}""")]
    public void Open_api_30_does_not_trust_null_in_a_value_list(string left, string right)
    {
        string document = """{"oneOf":[{"type":"object","properties":{"k":""" + left + ""","id":{"format":"uuid"}},"required":["k"]},{"type":"object","properties":{"k":""" + right + """},"required":["k"]}]}""";
        JsonValueSchema schema = Import(document);

        Assert.Contains("\"oneOf\"", schema.ToJsonSchema().ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\"oneOf\"", schema.ToJsonSchema(new JsonSchemaOptions { Target = JsonSchemaProfile.OpenApi30 }).ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Open_api_30_reads_the_enum_when_const_sits_beside_it()
    {
        const string Document = """{"oneOf":[{"type":"object","properties":{"k":{"const":"a","enum":["a","b"]},"id":{"format":"uuid"}},"required":["k"]},{"type":"object","properties":{"k":{"const":"b"}},"required":["k"]}]}""";

        Assert.DoesNotContain("\"oneOf\"", Import(Document).ToJsonSchema(new JsonSchemaOptions { Target = JsonSchemaProfile.OpenApi30 }).ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Open_api_30_drops_patternProperties_and_the_closing_with_them()
    {
        const string Document = """{"oneOf":[{"type":"object","properties":{"a":{"format":"uuid"}},"required":["a"]},{"type":"object","additionalProperties":false,"patternProperties":{"^z":{}},"properties":{"b":{}},"required":["b"]}]}""";

        Assert.DoesNotContain("\"oneOf\"", Import(Document).ToJsonSchema(new JsonSchemaOptions { Target = JsonSchemaProfile.OpenApi30 }).ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_rewriter_keeps_the_relaxed_form()
    {
        JsonSchemaProfile profile = JsonSchemaProfile.Draft202012.With(Rewriters.ConvertBooleanSchemas);
        string text = Import(Union(IdBranch, RuleBranch)).ToJsonSchema(new JsonSchemaOptions { Target = profile }).ToJsonString();

        Assert.DoesNotContain("\"oneOf\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_huge_union_stays_cheap_and_relaxes_past_the_budget()
    {
        string[] branches = [.. Enumerable.Range(0, 3_000).Select(i => Kind($"{i}", WithId))];
        JsonValueSchema schema = Import(Union(branches));
        string text = Timing.Within(TimeSpan.FromSeconds(10), () => schema.ToJsonSchema().ToJsonString());

        Assert.DoesNotContain("\"oneOf\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Numbers_past_a_double_count_as_equal()
    {
        const string Document = """{"oneOf":[{"type":"object","properties":{"k":{"const":1e400},"id":{"format":"uuid"}},"required":["k","id"]},{"type":"object","properties":{"k":{"const":2e400}},"required":["k"]}]}""";

        Assert.DoesNotContain("\"oneOf\"", Import(Document).ToJsonSchema().ToJsonString(), StringComparison.Ordinal);
    }

    private static readonly JsonValueSchema Loop = Z.Lazy("loop", () => Loop!);

    [Fact]
    public void A_reference_that_never_ends_excludes_nothing()
    {
        JsonValueSchema schema = Z.OneOf(
            Z.Object().Property("k", Loop).Property("id", Z.String().Uuid()),
            Z.Object().Property("k", Z.String().Const("b")).Property("id", Z.String().Uuid()));

        Assert.DoesNotContain("\"oneOf\"", schema.ToJsonSchema().ToJsonString(), StringComparison.Ordinal);
    }

    private sealed class Named
    {
        public required string Name { get; init; }

        public string Kind => "b";
    }

    private sealed class Identified
    {
        public required string Id { get; init; }
    }

    private sealed class Ruled
    {
        public required string Rule { get; init; }
    }

    private static readonly JsonSerializerOptions Reflection = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    [Fact]
    public void A_property_that_only_output_has_excludes_nothing_in_the_input_schema()
    {
        JsonValueSchema schema = Z.OneOf(
            Z.Object().Property("Kind", Z.String().Const("a")).Property("id", Z.String().Uuid()).Passthrough(),
            Schema.Object<Named>(Reflection).Property(named => named.Kind, kind => kind.Const("b")).Passthrough());

        Assert.DoesNotContain("\"oneOf\"", schema.ToJsonSchema().ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Names_that_ignore_case_exclude_nothing()
    {
        JsonSerializerOptions options = new(Reflection) { PropertyNameCaseInsensitive = true };
        JsonValueSchema schema = Z.OneOf(Schema.Object<Identified>(options), Schema.Object<Ruled>(options));

        Assert.DoesNotContain("\"oneOf\"", schema.ToJsonSchema().ToJsonString(), StringComparison.Ordinal);
    }

    private static readonly string[] Values = ["\"x\"", "\"y\"", "1", "1.0", "2", "true", "null", Uuid, "\"no\""];

    private static readonly string[] PropertySchemas =
    [
        """{"const":"x"}""", """{"const":"y"}""", """{"enum":["x","y"]}""", """{"enum":["y",2]}""", """{"const":1}""", """{"const":1.0}""", """{"const":null}""",
        """{"type":"string","format":"uuid"}""", """{"type":"string"}""", """{"format":"date"}""", "false", "true", """{"$ref":"#/$defs/x"}""", """{"$ref":"#/$defs/never"}""",
    ];

    private static readonly string[] Names = ["a", "b", "k"];

    /// <summary>
    /// Random branches, where every instance <c>SafeParse</c> accepts must pass the emitted schema in every profile. This is the
    /// promise that a kept <c>oneOf</c> could break, because a validator without <c>format</c> sees more of the branches match.
    /// </summary>
    [Fact]
    public void A_kept_one_of_never_rejects_what_parsing_accepts()
    {
        Random random = new(20261106);
        int kept = 0;
        int accepted = 0;

        for (int round = 0; round < 1_500; round++)
        {
            string document = RandomUnion(random);
            JsonValueSchema schema = Import(document);
            string[] instances = [.. Enumerable.Range(0, 24).Select(_ => RandomInstance(random)), "null", "\"s\"", "{}"];
            string[] passing = [.. instances.Where(instance => schema.SafeParse(instance).IsSuccess)];
            accepted += passing.Length;

            foreach (JsonSchemaProfile profile in Profiles)
            {
                string text = schema.ToJsonSchema(new JsonSchemaOptions { Target = profile }).ToJsonString();
                kept += text.Contains("\"oneOf\"", StringComparison.Ordinal) && document.Contains("format", StringComparison.Ordinal) ? 1 : 0;

                JsonNode node = JsonNode.Parse(text)!;
                node = profile.Dialect == JsonSchemaDialect.OpenApi30 ? EmissionReviewTests.ConvertOpenApi30(node)! : node;

                if (node is JsonObject root && !root.ContainsKey("$schema"))
                {
                    root["$schema"] = "https://json-schema.org/draft/2020-12/schema";
                }

                JsonSchema oracle = JsonSchema.FromText(node.ToJsonString(), new BuildOptions { SchemaRegistry = new SchemaRegistry() });

                foreach (string instance in passing)
                {
                    Assert.True(oracle.Evaluate(JsonElement.Parse(instance)).IsValid, $"{profile.Dialect} rejects {instance}\n{document}\n{text}");
                }
            }
        }

        Assert.True(kept > 500, $"only {kept} kept a oneOf, so this proves little");
        Assert.True(accepted > 1_000, $"only {accepted} accepted, so this proves little");
    }

    private static string RandomUnion(Random random)
    {
        IEnumerable<string> branches = Enumerable.Range(0, random.Next(2, 4)).Select(_ => RandomBranch(random));
        return $$"""{"$defs":{"x":{"const":"x"},"never":false},"oneOf":[{{string.Join(",", branches)}}]}""";
    }

    private static string RandomBranch(Random random)
    {
        List<string> parts = [];

        switch (random.Next(8))
        {
            case 0:
                parts.Add("""
                    "type":["object","null"]
                    """);
                break;
            case 1:
                break;
            default:
                parts.Add("""
                    "type":"object"
                    """);
                break;
        }

        string[] named = [.. Names.Where(_ => random.Next(3) != 0)];

        if (named.Length > 0)
        {
            parts.Add("\"properties\":{" + string.Join(",", named.Select(name => $"\"{name}\":{PropertySchemas[random.Next(PropertySchemas.Length)]}")) + "}");
        }

        string[] required = [.. Names.Where(_ => random.Next(2) == 0)];

        if (required.Length > 0)
        {
            parts.Add("\"required\":[" + string.Join(",", required.Select(name => $"\"{name}\"")) + "]");
        }

        switch (random.Next(5))
        {
            case 0:
            case 1:
                parts.Add("\"additionalProperties\":false");
                break;
            case 2:
                parts.Add("""
                    "additionalProperties":{"type":"string"}
                    """);
                break;
        }

        if (random.Next(6) == 0)
        {
            parts.Add("""
                "patternProperties":{"^a":{}}
                """);
        }

        return "{" + string.Join(",", parts.Select(part => part.Trim())) + "}";
    }

    private static string RandomInstance(Random random)
    {
        string[] present = [.. Names.Where(_ => random.Next(3) != 0)];
        return "{" + string.Join(",", present.Select(name => $"\"{name}\":{Values[random.Next(Values.Length)]}")) + "}";
    }
}
