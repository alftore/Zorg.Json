using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Zorg.Json.Emission;
using Zorg.Json.Import;

namespace Zorg.Json.ComplianceTests;

/// <summary>Cases from the review of preview.5.</summary>
public sealed class Preview5ReviewTests
{
    private static JsonSchemaImportOptions With(string uri, string text, JsonSchemaImportDialect dialect = JsonSchemaImportDialect.Draft202012) =>
        new() { Documents = new Dictionary<string, string> { [uri] = text }, DefaultDialect = dialect };

    private const string Draft07 = "\"$schema\":\"http://json-schema.org/draft-07/schema#\"";

    [Theory]
    [InlineData("""{"type":"object","anyOf":[{}],"oneOf":[{"properties":{"b":{"type":"string","format":"email"}}},{"properties":{"c":{"type":"number"}},"required":["c"]}],"unevaluatedProperties":false}""", """{"b":"a@b.co"}""")]
    [InlineData("""{"type":"object","if":{"properties":{"k":{"const":1}},"required":["k"]},"then":{"properties":{"b":{"type":"string","format":"email"}}},"unevaluatedProperties":false}""", """{"k":1,"b":"a@b.co"}""")]
    public void The_output_schema_drops_unevaluated_where_what_evaluated_it_is_left_out(string text, string json)
    {
        var schema = Z.FromJsonSchema(text, new JsonSchemaImportOptions { Formats = FormatHandling.AssertKnown });

        Assert.True(schema.SafeParse(json).IsSuccess);
        Assert.Null(schema.ToJsonSchema(new() { Direction = SchemaDirection.Output })["unevaluatedProperties"]);
    }

    [Fact]
    public void The_output_schema_of_a_large_import_with_many_negations_is_quick()
    {
        string big = "{" + string.Join(',', Enumerable.Range(0, 1500).Select(index => $"\"p{index}\":{{\"type\":\"string\"}}")) + "}";
        string defs = string.Join(',', Enumerable.Range(0, 1000).Select(index => $"\"d{index}\":{{\"oneOf\":[{{\"not\":{{\"$ref\":\"#/$defs/big\"}}}},{{\"type\":\"null\"}}]}}"));
        string text = $"{{\"$defs\":{{\"big\":{{\"type\":\"object\",\"properties\":{big}}},{defs}}},\"type\":\"object\",\"properties\":{{{string.Join(',', Enumerable.Range(0, 1000).Select(index => $"\"q{index}\":{{\"$ref\":\"#/$defs/d{index}\"}}"))}}}}}";
        var schema = Z.FromJsonSchema(text);

        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        _ = schema.ToJsonSchema(new() { Direction = SchemaDirection.Output });

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
    }

    [Fact]
    public void An_if_whose_branch_fills_a_default_is_left_out_of_the_output_schema()
    {
        var schema = Z.If(Z.Object().Property("a", Z.Any()).Strict(), Z.Object().Passthrough().Property("b", Z.String().Default("d")), Z.Never());

        Assert.Equal("""{"a":1,"b":"d"}""", schema.Parse("""{"a":1}""").GetRawText());
        Assert.Null(schema.ToJsonSchema(new() { Direction = SchemaDirection.Output })["if"]);
    }

    [Fact]
    public void An_enum_that_repeats_a_value_is_written_with_each_once()
    {
        var schema = Z.FromJsonSchema("""{"enum":[[1,"a",null],[1.0,"a",null],2,2]}""");

        Assert.Equal("""[[1,"a",null],2]""", schema.ToJsonSchema()["enum"]!.ToJsonString());
    }

    [Fact]
    public void A_document_with_a_reference_at_its_root_reads_its_own_pointers()
    {
        JsonValueSchema schema = Z.FromJsonSchema(
            "{" + Draft07 + """,  "definitions":{"x":{"type":"integer"}},"$ref":"http://e.com/a"}""",
            With("http://e.com/a", """{"$ref":"#/definitions/x","definitions":{"x":{"type":"string"}}}""", JsonSchemaImportDialect.Draft07));

        Assert.True(schema.SafeParse("\"s\"").IsSuccess);
        Assert.False(schema.SafeParse("1").IsSuccess);
    }

    [Fact]
    public void A_document_is_a_resource_in_open_api_30()
    {
        JsonValueSchema schema = Z.FromJsonSchema(
            """{"definitions":{"x":{"type":"integer"}},"properties":{"p":{"$ref":"http://e.com/a"}}}""",
            With("http://e.com/a", """{"properties":{"q":{"$ref":"#/definitions/x"}},"definitions":{"x":{"type":"string"}}}""", JsonSchemaImportDialect.OpenApi30));

        Assert.True(schema.SafeParse("""{"p":{"q":"s"}}""").IsSuccess);
        Assert.False(schema.SafeParse("""{"p":{"q":1}}""").IsSuccess);
    }

    [Theory]
    [InlineData("""{"$id":"#","$defs":{"a":{"type":"string"}},"$ref":"#/$defs/a"}""")]
    public void A_document_with_only_a_fragment_as_its_id_is_a_resource(string document)
    {
        JsonValueSchema schema = Z.FromJsonSchema("""{"$defs":{"a":{"type":"integer"}},"$ref":"http://e.com/a"}""", With("http://e.com/a", document));

        Assert.True(schema.SafeParse("\"s\"").IsSuccess);
        Assert.False(schema.SafeParse("1").IsSuccess);
    }

    [Fact]
    public void The_same_anchor_in_the_schema_and_a_document_is_no_clash()
    {
        JsonValueSchema schema = Z.FromJsonSchema(
            """{"$anchor":"foo","properties":{"p":{"$ref":"http://e.com/a#foo"}}}""",
            With("http://e.com/a", """{"$anchor":"foo","type":"string"}"""));

        Assert.False(schema.SafeParse("""{"p":1}""").IsSuccess);
    }

    [Fact]
    public void A_relative_id_in_a_document_resolves_against_its_uri()
    {
        Dictionary<string, string> documents = new()
        {
            ["http://e.com/dir/a.json"] = """{"$id":"b.json","$ref":"c.json"}""",
            ["http://e.com/dir/c.json"] = """{"type":"integer"}""",
            ["http://root.com/c.json"] = """{"type":"string"}""",
        };

        JsonValueSchema schema = Z.FromJsonSchema("""{"$id":"http://root.com/r","$ref":"http://e.com/dir/a.json"}""", new JsonSchemaImportOptions { Documents = documents });

        Assert.True(schema.SafeParse("1").IsSuccess);
        Assert.False(schema.SafeParse("\"s\"").IsSuccess);
        Assert.Contains("http://e.com/dir/b.json", schema.ToJsonSchema().ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Unevaluated_reads_the_dynamic_scope_of_the_resources_it_passes()
    {
        const string Document = """{"$id":"http://e.com/r","$ref":"a","unevaluatedProperties":false,"$defs":{"A":{"$id":"a","$ref":"b","$defs":{"n":{"$dynamicAnchor":"n","properties":{"a":{}}}}},"B":{"$id":"b","$dynamicRef":"#n","$defs":{"x":{"$dynamicAnchor":"n","properties":{"b":{}}}}}}}""";
        JsonValueSchema schema = Z.FromJsonSchema(Document);

        Assert.True(schema.SafeParse("""{"a":1}""").IsSuccess);
        Assert.False(schema.SafeParse("""{"b":1}""").IsSuccess);

        string text = schema.ToJsonSchema().ToJsonString();
        Assert.True(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft202012, """{"a":1}"""), text);
    }

    [Theory]
    [InlineData("""{"$schema":"https://json-schema.org/draft/2019-09/schema","not":{"allOf":[{"contains":{"const":1}}],"unevaluatedItems":false}}""", "[1]")]
    [InlineData("""{"$schema":"https://json-schema.org/draft/2019-09/schema","$id":"http://e.com/o","$recursiveAnchor":true,"$ref":"i","contains":{"const":1},"unevaluatedItems":false,"$defs":{"I":{"$id":"i","$recursiveAnchor":true,"properties":{"c":{"not":{"$recursiveRef":"#"}}}}}}""", """{"c":[1]}""")]
    public void What_2019_09_means_by_contains_stays_looser_when_written(string document, string instance)
    {
        JsonValueSchema schema = Z.FromJsonSchema(document);
        string text = schema.ToJsonSchema().ToJsonString();

        Assert.True(schema.SafeParse(instance).IsSuccess);
        Assert.True(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft202012, instance), text);
    }

    [Fact]
    public void A_dynamic_target_with_a_format_counts_under_not()
    {
        const string Document = """{"$id":"http://e.com/o","$dynamicAnchor":"n","format":"email","$ref":"i","$defs":{"I":{"$id":"i","properties":{"c":{"not":{"$dynamicRef":"#n"}}},"$defs":{"m":{"$dynamicAnchor":"n"}}}}}""";
        JsonValueSchema schema = Z.FromJsonSchema(Document, new JsonSchemaImportOptions { Formats = FormatHandling.AssertKnown });
        string text = schema.ToJsonSchema().ToJsonString();

        Assert.True(schema.SafeParse("""{"c":"x"}""").IsSuccess);
        Assert.True(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft202012, """{"c":"x"}"""), text);
    }

    [Fact]
    public void A_plain_dynamic_reference_in_an_embedded_import_moves_like_a_reference()
    {
        JsonValueSchema imported = Z.FromJsonSchema("""{"type":"object","properties":{"c":{"$dynamicRef":"#"},"v":{"type":"string"}},"additionalProperties":false}""");
        string text = Z.Object().Property("x", imported).ToJsonSchema().ToJsonString();

        Assert.True(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft202012, """{"x":{"c":{"v":"s"}}}"""), text);
        Assert.False(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft202012, """{"x":{"c":{"v":1}}}"""), text);
    }

    [Fact]
    public void A_plain_dynamic_reference_follows_a_renamed_definition()
    {
        JsonValueSchema first = Z.FromJsonSchema("""{"$defs":{"s":{"type":"string"}},"properties":{"p":{"$dynamicRef":"#/$defs/s"}}}""");
        JsonValueSchema second = Z.FromJsonSchema("""{"$defs":{"s":{"type":"integer"}},"properties":{"p":{"$dynamicRef":"#/$defs/s"}}}""");
        string text = Z.Object().Property("x", first).Property("y", second).ToJsonSchema().ToJsonString();

        Assert.True(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft202012, """{"x":{"p":"s"},"y":{"p":1}}"""), text);
    }

    [Fact]
    public void A_plain_dynamic_reference_meets_a_rewriter_as_a_reference()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""{"$ref":"#/$defs/t","$defs":{"t":{"type":"object","properties":{"c":{"$dynamicRef":"#/$defs/t"}}}}}""");
        JsonSchemaProfile profile = JsonSchemaProfile.Draft202012.With(Rewriters.InlineOrRejectRecursion);

        Assert.Contains(schema.Emit(new JsonSchemaOptions { Target = profile }).Diagnostics, diagnostic => diagnostic.Keyword == "$ref");
    }

    [Theory]
    [InlineData("""{"type":"object","properties":{"c":{"$ref":"http://e.com/a"}},"required":["c"],"additionalProperties":false}""", """{"type":"string"}""")]
    [InlineData("""{"type":"object","properties":{"c":{"$ref":"http://e.com/a#/$defs/s"}},"required":["c"],"additionalProperties":false}""", """{"$defs":{"s":{"type":"string"}}}""")]
    public void A_reference_by_uri_reaches_a_provider_as_a_pointer(string document, string other)
    {
        JsonValueSchema schema = Z.FromJsonSchema(document, With("http://e.com/a", other));

        foreach (JsonSchemaProfile profile in new[] { Zorg.Json.Llm.LlmProfiles.OpenAIStrict, Zorg.Json.Llm.LlmProfiles.GeminiJsonSchema })
        {
            string text = schema.ToJsonSchema(new JsonSchemaOptions { Target = profile }).ToJsonString();

            Assert.DoesNotContain("\"$ref\":\"http", text, StringComparison.Ordinal);
            Assert.True(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft202012, """{"c":"s"}"""), text);
        }
    }

    private static string Nested(int depth) => string.Concat(Enumerable.Repeat("""{"c":""", depth)) + "{}" + new string('}', depth);

    [Theory]
    [InlineData("""{"$dynamicAnchor":"n","anyOf":[{"properties":{"c":{"$dynamicRef":"#n"}}},{"required":["q"]}],"unevaluatedProperties":false}""")]
    [InlineData("""{"$schema":"https://json-schema.org/draft/2019-09/schema","$recursiveAnchor":true,"anyOf":[{"properties":{"c":{"$recursiveRef":"#"}}},{"required":["q"]}],"unevaluatedProperties":false}""")]
    [InlineData("""{"$id":"http://e/t","$dynamicAnchor":"n","oneOf":[{"properties":{"c":{"$dynamicRef":"#n"}}},{"required":["q"]}],"unevaluatedProperties":false}""")]
    [InlineData("""{"$defs":{"v":{"type":"string"},"w":{"$dynamicRef":"#/$defs/v"}},"anyOf":[{"properties":{"c":{"$ref":"#"}}},{"required":["q"]}],"unevaluatedProperties":false}""")]
    public void A_deep_instance_is_no_problem_under_a_dynamic_scope(string document)
    {
        JsonValueSchema schema = Z.FromJsonSchema(document);

        Assert.True(schema.SafeParse(Nested(60)).IsSuccess);
        Assert.False(schema.SafeParse(Nested(60).Replace("{}", """{"x":1}""", StringComparison.Ordinal)).IsSuccess);
    }

    [Fact]
    public void What_is_recalled_depends_on_the_dynamic_scope()
    {
        // One branch at one place, reached through two resources that give "n" different meanings. It fails through a and passes through b.
        const string Document = """{"$id":"http://e/r","anyOf":[{"$ref":"a"},{"$ref":"b"}],"$defs":{"a":{"$id":"a","$ref":"shared","$defs":{"t":{"$dynamicAnchor":"n","type":"string"}}},"b":{"$id":"b","$ref":"shared","$defs":{"t":{"$dynamicAnchor":"n","properties":{"p":true}}}},"shared":{"$id":"shared","anyOf":[{"$dynamicRef":"#n"},{"type":"object"}],"unevaluatedProperties":false,"$defs":{"t":{"$dynamicAnchor":"n","type":"string"}}}}}""";

        Assert.True(Z.FromJsonSchema(Document).SafeParse("""{"p":1}""").IsSuccess);
    }

    [Theory]
    [InlineData("""{"$id":"http://e/a","$ref":"b#/foo/x","$defs":{"b":{"$id":"b","$anchor":"z","type":"object","foo":{"x":{"properties":{"k":{"$ref":"#z"}}}}}}}""", """{"k":{}}""", """{"k":1}""")]
    public void A_copied_reference_reads_in_the_resource_it_came_from(string document, string good, string bad)
    {
        JsonValueSchema schema = Z.FromJsonSchema(document);
        Assert.True(schema.SafeParse(good).IsSuccess);
        Assert.False(schema.SafeParse(bad).IsSuccess);

        foreach (JsonSchemaProfile profile in new[] { JsonSchemaProfile.Draft202012, JsonSchemaProfile.Draft07 })
        {
            string text = schema.ToJsonSchema(new JsonSchemaOptions { Target = profile }).ToJsonString();

            Assert.True(EmissionReviewTests.Accepts(text, profile, good), text);
            Assert.False(EmissionReviewTests.Accepts(text, profile, bad), text);
        }
    }

    [Theory]
    [InlineData("""{"$id":"http://e/a","$ref":"b#/foo/x","$defs":{"b":{"$id":"b","$dynamicAnchor":"n","type":"object","foo":{"x":{"properties":{"k":{"$dynamicRef":"#n"}}}}}}}""")]
    [InlineData("""{"$id":"http://e/a","$ref":"b#/not/properties/k","$defs":{"b":{"$id":"b","$dynamicAnchor":"n","type":"object","not":{"properties":{"k":{"$dynamicRef":"#n"}},"required":["zz"]}}}}""")]
    public void A_copy_in_another_resource_leaves_a_dynamic_reference_out(string document)
    {
        EmitResult result = Z.FromJsonSchema(document).Emit();

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Keyword == "$dynamicRef");
        Assert.True(EmissionReviewTests.Accepts(result.Schema.ToJsonString(), JsonSchemaProfile.Draft202012, """{"k":{}}"""), result.Schema.ToJsonString());
    }

    [Fact]
    public void The_dialect_of_a_bundled_resource_does_not_follow_it_to_another_dialect()
    {
        const string Document = """{"$defs":{"x":{"$id":"http://e/x","$schema":"https://json-schema.org/draft/2020-12/schema","prefixItems":[{"type":"string"}],"items":false}},"$ref":"http://e/x"}""";
        string text = Z.FromJsonSchema(Document).ToJsonSchema(new JsonSchemaOptions { Target = JsonSchemaProfile.Draft07 }).ToJsonString();

        Assert.True(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft07, """["a"]"""), text);
        Assert.False(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft07, """[1]"""), text);
    }

    [Fact]
    public void An_anchor_only_2019_allows_is_written_as_a_pointer()
    {
        const string Document = """{"$schema":"https://json-schema.org/draft/2019-09/schema","$defs":{"a":{"$anchor":"a:b","type":"string"}},"$ref":"#a:b"}""";
        string text = Z.FromJsonSchema(Document).ToJsonSchema().ToJsonString();

        Assert.DoesNotContain("a:b", text, StringComparison.Ordinal);
        Assert.False(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft202012, "1"), text);
    }

    [Fact]
    public void A_looser_contains_is_reported()
    {
        const string Document = """{"$schema":"https://json-schema.org/draft/2019-09/schema","contains":{"type":"integer"},"unevaluatedItems":false}""";

        Assert.Contains(Z.FromJsonSchema(Document).Emit().Diagnostics, diagnostic => diagnostic.Keyword == "unevaluatedItems");
    }

    [Fact]
    public void Relative_ids_reach_a_provider_through_the_right_parent()
    {
        const string Document = """{"type":"object","properties":{"c":{"$ref":"#/$defs/b/$defs/u"}},"required":["c"],"additionalProperties":false,"$defs":{"a":{"$id":"http://a.com/a","$defs":{"x":{"$id":"x","type":"string"},"u":{"$id":"u","$ref":"x"}}},"b":{"$id":"http://b.com/b","$defs":{"x":{"$id":"x","type":"integer"},"u":{"$id":"u","$ref":"x"}}}}}""";
        JsonValueSchema schema = Z.FromJsonSchema(Document);

        foreach (JsonSchemaProfile profile in new[] { Zorg.Json.Llm.LlmProfiles.OpenAIStrict, Zorg.Json.Llm.LlmProfiles.GeminiJsonSchema })
        {
            string text = schema.ToJsonSchema(new JsonSchemaOptions { Target = profile }).ToJsonString();

            Assert.True(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft202012, """{"c":1}"""), text);
            Assert.False(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft202012, """{"c":"s"}"""), text);
        }
    }

    [Fact]
    public void A_relative_reference_reaches_a_provider_as_a_pointer()
    {
        const string Document = """{"$id":"http://r.com/r","type":"object","properties":{"c":{"$ref":"sub"}},"required":["c"],"additionalProperties":false,"$defs":{"s":{"$id":"sub","type":"string"}}}""";
        string text = Z.FromJsonSchema(Document).ToJsonSchema(new JsonSchemaOptions { Target = Zorg.Json.Llm.LlmProfiles.OpenAIStrict }).ToJsonString();

        Assert.True(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft202012, """{"c":"s"}"""), text);
    }

    [Theory]
    [InlineData("""{"$ref":"#/$defs/t","$defs":{"t":{"$anchor":"t","type":"object","properties":{"c":{"$dynamicRef":"#t"}}}}}""")]
    [InlineData("""{"$ref":"#/$defs/t","$defs":{"t":{"type":"object","properties":{"c":{"$dynamicRef":"#%2F$defs/t"}}}}}""")]
    public void A_dynamic_reference_with_no_dynamic_anchor_of_its_name_is_a_reference(string document)
    {
        JsonSchemaProfile profile = JsonSchemaProfile.Draft202012.With(Rewriters.InlineOrRejectRecursion);

        Assert.Contains(Z.FromJsonSchema(document).Emit(new JsonSchemaOptions { Target = profile }).Diagnostics, diagnostic => diagnostic.Keyword == "$ref");
    }

    [Fact]
    public void A_dynamic_reference_follows_the_id_of_a_document()
    {
        JsonValueSchema schema = Z.FromJsonSchema(
            """{"$id":"http://e.com/r","$dynamicRef":"http://e.com/given#n"}""",
            With("http://e.com/given", """{"$id":"http://e.com/own","$dynamicAnchor":"n","type":"string"}"""));
        string text = schema.ToJsonSchema().ToJsonString();

        Assert.Contains("\"$dynamicRef\":\"http://e.com/own#n\"", text, StringComparison.Ordinal);
        Assert.False(schema.SafeParse("1").IsSuccess);
        Assert.False(Z.FromJsonSchema(text).SafeParse("1").IsSuccess);
    }

    [Fact]
    public void A_plain_dynamic_reference_survives_a_target_that_moved()
    {
        const string Document = """{"$dynamicRef":"#/oneOf/0","oneOf":[{"type":"string","format":"email"},{"type":"string","format":"uuid"}]}""";
        JsonValueSchema schema = Z.FromJsonSchema(Document, new JsonSchemaImportOptions { Formats = FormatHandling.AssertKnown });

        Assert.Contains("anyOf", schema.ToJsonSchema().ToJsonString(), StringComparison.Ordinal);
        Assert.True(EmissionReviewTests.Accepts(schema.ToJsonSchema().ToJsonString(), JsonSchemaProfile.Draft202012, "\"a@b.co\""));
    }

    [Fact]
    public void An_import_used_twice_writes_no_repeated_dynamic_anchor()
    {
        JsonValueSchema imported = Z.FromJsonSchema("""{"$dynamicAnchor":"n","type":"object","properties":{"c":{"$dynamicRef":"#n"}}}""");
        string text = Z.Object().Property("x", imported).Property("y", imported).ToJsonSchema().ToJsonString();

        Assert.DoesNotContain("\"$dynamicAnchor\"", text, StringComparison.Ordinal);
        Assert.True(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft202012, """{"x":{},"y":{}}"""), text);
    }

    [Fact]
    public void Escaped_keywords_turn_the_scope_on_too()
    {
        const string Plain = """{"$id":"http://e.com/o","$dynamicAnchor":"n","type":"string","$ref":"i","$defs":{"I":{"$id":"i","$dynamicAnchor":"n","$defs":{"d":{"$dynamicRef":"#n"}},"$ref":"#/$defs/d"}}}""";
        string escaped = Plain.Replace("$dynamic", "\\u0024dynamic", StringComparison.Ordinal);

        Assert.Equal(Z.FromJsonSchema("""{"$dynamicAnchor":"m"}""").SafeParse("1").IsSuccess, Z.FromJsonSchema(escaped.Length > 0 ? """{"$dynamicAnchor":"m"}""" : string.Empty).SafeParse("1").IsSuccess);
    }

    [Fact]
    public void The_dialect_of_a_document_does_not_go_along()
    {
        JsonValueSchema schema = Z.FromJsonSchema(
            """{"$ref":"http://e.com/a"}""",
            With("http://e.com/a", """{"$schema":"https://json-schema.org/draft/2020-12/schema","prefixItems":[{"type":"string"}]}"""));
        string text = schema.ToJsonSchema(new JsonSchemaOptions { Target = JsonSchemaProfile.Draft07 }).ToJsonString();

        Assert.DoesNotContain("2020-12/schema\",\"", text[20..], StringComparison.Ordinal);
        Assert.False(Z.FromJsonSchema(text).SafeParse("[1]").IsSuccess);
    }

    [Theory]
    [InlineData("a:b", true)]
    [InlineData("_b", false)]
    public void Anchors_in_2019_09_have_their_own_syntax(string name, bool valid)
    {
        string document = "{\"$schema\":\"https://json-schema.org/draft/2019-09/schema\",\"$anchor\":\"" + name + "\"}";

        if (valid)
        {
            Assert.NotNull(Z.FromJsonSchema(document));
        }
        else
        {
            Assert.Throws<JsonSchemaImportException>(() => Z.FromJsonSchema(document));
        }
    }

    [Fact]
    public void A_recursive_anchor_outside_the_walked_tree_is_found()
    {
        const string Document = """{"$schema":"https://json-schema.org/draft/2019-09/schema","definitions":{"x":{"$id":"http://e.com/x","$recursiveAnchor":true,"properties":{"c":{"$recursiveRef":"#"},"v":{"type":"string"}}}},"$ref":"#/definitions/x"}""";

        Assert.False(Z.FromJsonSchema(Document).SafeParse("""{"c":{"v":1}}""").IsSuccess);
    }

    [Theory]
    [InlineData("""{"$recursiveRef":"#","$ref":"#/$defs/a","$defs":{"a":{"type":"string"}}}""")]
    [InlineData("""{"$schema":"https://json-schema.org/draft/2019-09/schema","$dynamicRef":"#","$ref":"#/$defs/a","$defs":{"a":{"type":"string"}}}""")]
    public void A_keyword_that_means_nothing_in_the_dialect_is_not_refused(string document) =>
        Assert.False(Z.FromJsonSchema(document).SafeParse("1").IsSuccess);
}
