using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Xunit;
using Zorg.Json.Emission;
using Zorg.Json.Import;

namespace Zorg.Json.ComplianceTests;

/// <summary>Cases from the review of preview.4. Each emitted schema is judged by JsonSchema.Net.</summary>
public sealed class Preview4ReviewTests
{
    private static JsonValueSchema Asserting(string document) => Z.FromJsonSchema(document, new JsonSchemaImportOptions { Formats = FormatHandling.AssertKnown });

    [Theory]
    [InlineData("""{"anyOf":[true],"oneOf":[{"format":"email","properties":{"a":true}},{"type":"string"}],"unevaluatedProperties":false}""", """{"a":1}""")]
    [InlineData("""{"allOf":[{"anyOf":[true],"oneOf":[{"format":"email","properties":{"a":true}},{"type":"string"}]}],"unevaluatedProperties":false}""", """{"a":1}""")]
    [InlineData("""{"$defs":{"d":{"anyOf":[true],"oneOf":[{"format":"email","prefixItems":[true]},{"type":"string"}]}},"$ref":"#/$defs/d","unevaluatedItems":false}""", "[1]")]
    public void A_left_out_one_of_takes_unevaluated_with_it(string document, string instance)
    {
        JsonValueSchema schema = Asserting(document);
        string text = schema.ToJsonSchema().ToJsonString();

        Assert.True(schema.SafeParse(instance).IsSuccess);
        Assert.True(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft202012, instance), text);
    }

    [Fact]
    public void Nested_ids_with_the_same_text_under_different_bases_keep_their_text()
    {
        const string Document = """{"$id":"http://x.test/","$defs":{"p":{"$id":"p/","$defs":{"a":{"$id":"a","type":"string"}},"$ref":"a"},"q":{"$id":"q/","$defs":{"a":{"$id":"a","type":"number"}},"$ref":"a"}},"properties":{"s":{"$ref":"p/"},"n":{"$ref":"q/"}}}""";
        string text = Z.FromJsonSchema(Document).ToJsonSchema().ToJsonString();

        Assert.DoesNotContain("a-2", text, StringComparison.Ordinal);
        Assert.True(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft202012, """{"s":"x","n":1}"""), text);
        Assert.False(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft202012, """{"s":1,"n":"x"}"""), text);
    }

    [Theory]
    [InlineData("http://x.test/r", "http://x.test/a")]
    [InlineData(null, "http://x.test/a")]
    public void Two_imports_with_the_same_ids_keep_their_own_definitions(string? root, string id)
    {
        string head = root is null ? "{" : $$"""{"$id":"{{root}}",""";
        string first = head + "\"$defs\":{\"a\":{\"$id\":\"" + id + "\",\"type\":\"string\"}},\"properties\":{\"p\":{\"$ref\":\"" + id + "\"}}}";
        string second = head + "\"$defs\":{\"a\":{\"$id\":\"" + id + "\",\"type\":\"number\"}},\"properties\":{\"p\":{\"$ref\":\"" + id + "\"}}}";
        ObjectSchema outer = Z.Object().Property("x", Z.FromJsonSchema(first)).Property("y", Z.FromJsonSchema(second));
        string text = outer.ToJsonSchema().ToJsonString();

        Assert.True(outer.SafeParse("""{"x":{"p":"s"},"y":{"p":1}}""").IsSuccess);
        Assert.True(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft202012, """{"x":{"p":"s"},"y":{"p":1}}"""), text);
        Assert.False(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft202012, """{"x":{"p":1},"y":{"p":1}}"""), text);
    }

    [Fact]
    public void Two_imports_with_different_bases_and_the_same_nested_id_resolve()
    {
        const string A = """{"$id":"http://x.test/p/r","$defs":{"a":{"$id":"a","type":"string"}},"properties":{"p":{"$ref":"a"}}}""";
        const string B = """{"$id":"http://x.test/q/r","$defs":{"a":{"$id":"a","type":"number"}},"properties":{"p":{"$ref":"a"}}}""";
        string text = Z.Object().Property("x", Z.FromJsonSchema(A)).Property("y", Z.FromJsonSchema(B)).ToJsonSchema().ToJsonString();

        Assert.True(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft202012, """{"x":{"p":"s"},"y":{"p":1}}"""), text);
    }

    [Fact]
    public void A_fragment_that_crosses_a_nested_id_stays_true()
    {
        const string Document = """{"$id":"http://x.test/root","$defs":{"inner":{"$id":"http://x.test/inner","$defs":{"y":{"type":"string"}}}},"properties":{"p":{"$ref":"http://x.test/root#/$defs/inner/$defs/y"}}}""";

        foreach (JsonSchemaProfile profile in new[] { JsonSchemaProfile.Draft202012, JsonSchemaProfile.Draft07, JsonSchemaProfile.OpenApi31 })
        {
            string text = Z.FromJsonSchema(Document).ToJsonSchema(new JsonSchemaOptions { Target = profile }).ToJsonString();

            Assert.True(EmissionReviewTests.Accepts(text, profile, """{"p":"s"}"""), text);
            Assert.False(EmissionReviewTests.Accepts(text, profile, """{"p":1}"""), text);
        }
    }

    [Theory]
    [InlineData("""{"type":"string","format":"email"}""", "\"nope\"")]
    [InlineData("""{"oneOf":[{"format":"email"},{"type":"number"}]}""", "\"x\"")]
    [InlineData("""{"if":{"format":"email"},"then":true,"else":false}""", "\"x\"")]
    public void An_annotated_format_is_not_written_where_validators_may_check_it(string document, string instance)
    {
        JsonValueSchema schema = Z.FromJsonSchema(document);

        Assert.True(schema.SafeParse(instance).IsSuccess);

        foreach (JsonSchemaProfile profile in new[] { JsonSchemaProfile.Draft07, JsonSchemaProfile.OpenApi30 })
        {
            EmitResult result = schema.Emit(new JsonSchemaOptions { Target = profile });

            Assert.DoesNotContain("\"format\"", result.Schema.ToJsonString(), StringComparison.Ordinal);
            Assert.NotEmpty(result.Diagnostics);
        }

        Assert.Contains("\"format\"", schema.ToJsonSchema().ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_asserted_format_is_still_written_to_draft_07()
    {
        string text = Asserting("""{"type":"string","format":"email"}""").ToJsonSchema(new JsonSchemaOptions { Target = JsonSchemaProfile.Draft07 }).ToJsonString();

        Assert.Contains("\"format\":\"email\"", text, StringComparison.Ordinal);
    }

    private static JsonSchemaProfile Relocating(params string[] keywords) =>
        JsonSchemaProfile.Draft202012.With(Rewriters.RelocateToDescription(keywords));

    [Theory]
    [InlineData("""{"allOf":[{"properties":{"b":true}}],"unevaluatedProperties":false}""", "allOf", """{"b":1}""")]
    [InlineData("""{"anyOf":[{"properties":{"b":true}}],"unevaluatedProperties":false}""", "anyOf", """{"b":1}""")]
    [InlineData("""{"dependentSchemas":{"b":{"properties":{"b":true}}},"unevaluatedProperties":false}""", "dependentSchemas", """{"b":1}""")]
    [InlineData("""{"patternProperties":{"^b":true},"unevaluatedProperties":false}""", "patternProperties", """{"b":1}""")]
    [InlineData("""{"contains":{"type":"string"},"unevaluatedItems":false}""", "contains", """["x"]""")]
    [InlineData("""{"prefixItems":[{}],"unevaluatedItems":false}""", "prefixItems", "[1]")]
    [InlineData("""{"patternProperties":{"^a":true},"additionalProperties":false}""", "patternProperties", """{"a1":1}""")]
    [InlineData("""{"prefixItems":[{}],"items":{"const":"x"}}""", "prefixItems", "[null]")]
    public void Moving_what_evaluates_takes_what_depends_on_it(string document, string keyword, string instance)
    {
        JsonValueSchema schema = Z.FromJsonSchema(document);
        string text = schema.ToJsonSchema(new JsonSchemaOptions { Target = Relocating(keyword) }).ToJsonString();

        Assert.True(schema.SafeParse(instance).IsSuccess);
        Assert.True(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft202012, instance), text);
    }

    [Theory]
    [InlineData("""{"$defs":{"d":{"properties":{"a":true}}},"allOf":[{"$ref":"#/$defs/d","properties":{"b":true}}],"unevaluatedProperties":false}""", """{"a":1,"b":1}""")]
    [InlineData("""{"$defs":{"d":{"type":"object"}},"not":{"$ref":"#/$defs/d","required":["a"]}}""", "{}")]
    public void Inlining_keeps_what_sits_beside_a_reference(string document, string instance)
    {
        JsonValueSchema schema = Z.FromJsonSchema(document);
        JsonSchemaProfile profile = JsonSchemaProfile.Draft202012.With(Rewriters.InlineOrRejectRecursion);
        string text = schema.ToJsonSchema(new JsonSchemaOptions { Target = profile }).ToJsonString();

        Assert.True(schema.SafeParse(instance).IsSuccess);
        Assert.True(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft202012, instance), text);
    }

    [Fact]
    public void Inlining_a_plain_reference_adds_no_all_of()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""{"$defs":{"d":{"type":"string"}},"properties":{"p":{"$ref":"#/$defs/d"}}}""");
        JsonSchemaProfile profile = JsonSchemaProfile.Draft202012.With(Rewriters.InlineOrRejectRecursion);

        Assert.DoesNotContain("allOf", schema.ToJsonSchema(new JsonSchemaOptions { Target = profile }).ToJsonString(), StringComparison.Ordinal);
    }

    private static string Nested(string next, int depth)
    {
        string document = """{"v":1}""";

        for (int level = 0; level < depth; level++)
        {
            document = "{\"v\":1,\"" + next + "\":" + document + "}";
        }

        return document;
    }

    [Theory]
    [InlineData("anyOf")]
    [InlineData("oneOf")]
    [InlineData("allOf")]
    public void A_recursive_list_under_unevaluated_is_not_exponential(string keyword)
    {
        JsonValueSchema schema = Z.FromJsonSchema("""{"$defs":{"n":{""" + "\"" + keyword + "\":" + """[{"properties":{"v":{"type":"number"},"next":{"$ref":"#/$defs/n"}}}],"unevaluatedProperties":false}},"$ref":"#/$defs/n"}""");

        Assert.True(schema.SafeParse(Nested("next", 40)).IsSuccess);
        Assert.False(schema.SafeParse(Nested("other", 3)).IsSuccess);
    }

    [Fact]
    public void A_repeated_name_is_judged_by_its_own_value()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""{"properties":{"a":{"anyOf":[{"required":["x"],"properties":{"x":true}},{"required":["y"]}],"unevaluatedProperties":false}}}""");

        Assert.True(schema.SafeParse("""{"a":{"x":1}}""").IsSuccess);
        Assert.False(schema.SafeParse("""{"a":{"x":1},"a":{"y":1}}""").IsSuccess);
    }

    [Fact]
    public void Items_of_contains_are_judged_one_by_one()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""{"contains":{"anyOf":[{"required":["x"],"properties":{"x":true}},{"required":["y"]}],"unevaluatedProperties":false}}""");

        Assert.True(schema.SafeParse("""[{"y":1},{"x":1}]""").IsSuccess);
        Assert.False(schema.SafeParse("""[{"y":1}]""").IsSuccess);
    }

    [Fact]
    public void A_wide_object_pays_for_its_width()
    {
        StringBuilder document = new("""{"$defs":{"d0":{}""");

        for (int level = 1; level <= 16; level++)
        {
            document.Append(System.Globalization.CultureInfo.InvariantCulture, $",\"d{level}\":{{\"allOf\":[{{\"$ref\":\"#/$defs/d{level - 1}\"}},{{\"$ref\":\"#/$defs/d{level - 1}\"}}],\"properties\":{{\"z\":{{}}}}}}");
        }

        document.Append("""},"$ref":"#/$defs/d16"}""");
        JsonValueSchema schema = Z.FromJsonSchema(document.ToString(), new JsonSchemaImportOptions { MaxNodes = 10_000_000 });
        string instance = "{" + string.Join(",", Enumerable.Range(0, 10_000).Select(index => $"\"n{index}\":1")) + "}";
        Stopwatch watch = Stopwatch.StartNew();

        _ = schema.SafeParse(instance);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
    }

    [Fact]
    public void A_draft_07_id_that_spells_its_place_is_read()
    {
        const string Document = """{"$schema":"http://json-schema.org/draft-07/schema#","$id":"http://x.test/r","definitions":{"a":{"$id":"#/definitions/a","type":"string"}},"properties":{"p":{"$ref":"#/definitions/a"}}}""";
        JsonValueSchema schema = Z.FromJsonSchema(Document);

        Assert.True(schema.SafeParse("""{"p":"x"}""").IsSuccess);
        Assert.False(schema.SafeParse("""{"p":1}""").IsSuccess);
    }

    [Fact]
    public void An_id_with_an_empty_fragment_joins_an_anchor_in_draft_07()
    {
        const string Document = """{"$id":"http://x.test/y#","$anchor":"a","properties":{"p":{"$ref":"#a"}},"type":"object"}""";
        string text = Z.FromJsonSchema(Document).ToJsonSchema(new JsonSchemaOptions { Target = JsonSchemaProfile.Draft07 }).ToJsonString();

        Assert.Contains("\"$id\":\"http://x.test/y#a\"", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"$id":"vsmacros://x","$ref":"/abs"}""")]
    [InlineData("""{"$id":"vsmacros://x","$ref":"/"}""")]
    [InlineData("""{"$id":"vsmacros://x","$ref":"\\a"}""")]
    public void A_uri_that_dotnet_can_not_resolve_is_a_refusal(string document)
    {
        JsonSchemaImportException exception = Assert.Throws<JsonSchemaImportException>(() => Z.FromJsonSchema(document));

        Assert.Equal(JsonSchemaImportCodes.UnresolvedRef, exception.Code);
    }

    [Theory]
    [InlineData("""{"$id":"http://x.test/a","$defs":{"b":{"$id":"http://x.test/a","type":"string"}}}""")]
    [InlineData("""{"$defs":{"a":{"$id":"http://x.test/a"},"b":{"$id":"http://x.test/a"}}}""")]
    public void Two_resources_can_not_share_a_uri(string document)
    {
        JsonSchemaImportException exception = Assert.Throws<JsonSchemaImportException>(() => Z.FromJsonSchema(document));

        Assert.Equal(JsonSchemaImportCodes.InvalidSchema, exception.Code);
    }

    [Fact]
    public void The_placeholder_base_is_not_a_name()
    {
        JsonSchemaImportException exception = Assert.Throws<JsonSchemaImportException>(() => Z.FromJsonSchema("""{"$defs":{"b":{}},"$ref":"https://zorg.invalid/#/$defs/b"}"""));

        Assert.Equal(JsonSchemaImportCodes.UnresolvedRef, exception.Code);
    }

    [Fact]
    public void An_id_inside_a_target_under_an_unknown_keyword_is_found()
    {
        const string Document = """{"$id":"http://x.test/r","definitions":{"a":{"$id":"http://x.test/a/","$defs":{"b":{"$id":"b","type":"string"}},"$ref":"b"}},"properties":{"p":{"$ref":"#/definitions/a"}}}""";
        JsonValueSchema schema = Z.FromJsonSchema(Document);

        Assert.True(schema.SafeParse("""{"p":"x"}""").IsSuccess);
        Assert.False(schema.SafeParse("""{"p":1}""").IsSuccess);
    }

    [Fact]
    public void A_relative_reference_between_resources_of_a_document_without_an_id_stays_relative()
    {
        const string Document = """{"$defs":{"a":{"$id":"a/b.json","properties":{"up":{"$ref":"../c.json"}}},"c":{"$id":"c.json","type":"string"}},"properties":{"down":{"$ref":"a/b.json"}}}""";
        JsonValueSchema schema = Z.FromJsonSchema(Document);

        Assert.False(schema.SafeParse("""{"down":{"up":1}}""").IsSuccess);

        JsonValueSchema again = Z.FromJsonSchema(schema.ToJsonSchema().ToJsonString());
        Assert.False(again.SafeParse("""{"down":{"up":1}}""").IsSuccess);
        Assert.True(again.SafeParse("""{"down":{"up":"s"}}""").IsSuccess);
    }

    [Theory]
    [InlineData("""{"if":true,"then":{"properties":{"x":{}}},"unevaluatedProperties":false}""", "then", """{"x":1}""")]
    [InlineData("""{"if":false,"else":{"prefixItems":[{}]},"unevaluatedItems":false}""", "else", "[1]")]
    [InlineData("""{"allOf":[{"patternProperties":{"^x":{}}}],"unevaluatedProperties":false}""", "patternProperties", """{"x1":1}""")]
    [InlineData("""{"allOf":[{"anyOf":[{"properties":{"x":{}}}]}],"unevaluatedProperties":false}""", "anyOf", """{"x":1}""")]
    [InlineData("""{"allOf":[{"contains":{"const":1}}],"unevaluatedItems":false}""", "contains", "[1]")]
    [InlineData("""{"$defs":{"a":{"properties":{"k":{}},"if":{"required":["k"]},"then":{"properties":{"x":{}}}}},"$ref":"#/$defs/a","unevaluatedProperties":false}""", "if", """{"k":1,"x":1}""")]
    [InlineData("""{"contains":{"const":1},"minContains":0}""", "minContains", "[2]")]
    [InlineData("""{"$defs":{"d":{"not":{}}},"unevaluatedProperties":{"not":{"$ref":"#/$defs/d"}}}""", "not", """{"b":{}}""")]
    [InlineData("""{"$defs":{"d":{"const":1}},"unevaluatedItems":{"not":{"$ref":"#/$defs/d"}}}""", "const", "[2]")]
    public void Moving_anything_that_evaluates_takes_unevaluated_with_it(string document, string keyword, string instance)
    {
        JsonValueSchema schema = Z.FromJsonSchema(document);
        string text = schema.ToJsonSchema(new JsonSchemaOptions { Target = Relocating(keyword) }).ToJsonString();

        Assert.True(schema.SafeParse(instance).IsSuccess);
        Assert.True(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft202012, instance), text);
    }

    [Theory]
    [InlineData("""{"$ref":"#/$defs/t","$defs":{"t":{"properties":{"a":{"not":{"$ref":"#/$defs/t"}}}}}}""", """{"a":{"a":1}}""")]
    [InlineData("""{"$ref":"#/$defs/t","$defs":{"t":{"properties":{"a":{"if":{"$ref":"#/$defs/t"},"then":false}}}}}""", """{"a":{"a":1}}""")]
    [InlineData("""{"$ref":"#/$defs/t","$defs":{"t":{"type":"object","properties":{"a":{"oneOf":[{"$ref":"#/$defs/t"},{"type":"object"}]}}}}}""", """{"a":{"a":1}}""")]
    [InlineData("""{"$ref":"#/$defs/t","$defs":{"t":{"type":"array","contains":{"$ref":"#/$defs/t"},"minContains":0,"maxContains":1}}}""", "[1,[]]")]
    public void A_reference_that_can_not_be_inlined_takes_the_negative_keyword_with_it(string document, string instance)
    {
        JsonValueSchema schema = Z.FromJsonSchema(document);
        JsonSchemaProfile profile = JsonSchemaProfile.Draft202012.With(Rewriters.InlineOrRejectRecursion);
        string text = schema.ToJsonSchema(new JsonSchemaOptions { Target = profile }).ToJsonString();

        Assert.True(schema.SafeParse(instance).IsSuccess);
        Assert.True(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft202012, instance), text);
    }

    [Fact]
    public void Inlining_keeps_a_format_that_is_only_an_annotation()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""{"$defs":{"a":{}},"$ref":"#/$defs/a","format":"date"}""");
        JsonSchemaProfile profile = JsonSchemaProfile.Draft07.With(Rewriters.InlineOrRejectRecursion);

        Assert.DoesNotContain("\"format\"", schema.ToJsonSchema(new JsonSchemaOptions { Target = profile }).ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_reference_used_beside_its_own_target_is_not_recursive()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""{"$defs":{"a":{"type":"object"}},"$ref":"#/$defs/a","properties":{"x":{"$ref":"#/$defs/a"}}}""");
        JsonSchemaProfile profile = JsonSchemaProfile.Draft202012.With(Rewriters.InlineOrRejectRecursion);

        Assert.DoesNotContain(schema.Emit(new JsonSchemaOptions { Target = profile }).Diagnostics, diagnostic => diagnostic.Keyword == "$ref");
    }

    [Fact]
    public void An_id_of_only_a_hash_stays_as_it_is()
    {
        const string Document = """{"$id":"http://x.test/r","$defs":{"a":{"$id":"#","$ref":"#/$defs/b"},"b":{"type":"string"}},"properties":{"p":{"$ref":"#/$defs/a"}}}""";
        string text = Z.FromJsonSchema(Document).ToJsonSchema().ToJsonString();

        Assert.DoesNotContain("-2", text, StringComparison.Ordinal);
        Assert.False(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft202012, """{"p":1}"""), text);
    }

    [Fact]
    public void Remembered_verdicts_have_a_limit()
    {
        string branches = string.Join(",", Enumerable.Range(0, 20).Select(index => $"{{\"title\":\"t{index}\"}}"));
        JsonValueSchema schema = Z.FromJsonSchema("{\"anyOf\":[" + branches + "],\"allOf\":[{\"items\":{\"$ref\":\"#\"}}],\"unevaluatedItems\":false}");
        string document = "[]";

        for (int depth = 0; depth < 40; depth++)
        {
            document = "[" + document + "]";
        }

        document = "[" + string.Join(",", Enumerable.Repeat(document, 300)) + "]";
        long before = GC.GetTotalAllocatedBytes(precise: false);

        _ = schema.SafeParse(document);

        Assert.True(GC.GetTotalAllocatedBytes(precise: false) - before < 2_000_000_000);
    }

    [Theory]
    [InlineData("./")]
    [InlineData("x/../")]
    public void An_id_that_resolves_to_the_directory_of_the_document_is_no_clash(string id)
    {
        JsonValueSchema schema = Z.FromJsonSchema("{\"$defs\":{\"a\":{\"$id\":\"" + id + "\",\"type\":\"string\"}},\"properties\":{\"p\":{\"$ref\":\"./\"}}}");

        Assert.False(schema.SafeParse("""{"p":1}""").IsSuccess);
    }

    [Theory]
    [InlineData("""{"properties":{"a":{"$ref":"#/x-a"},"b":{"$ref":"http://x.test/c"}},"x-a":{"$id":"http://x.test/c","type":"string"}}""")]
    [InlineData("""{"properties":{"b":{"$ref":"http://x.test/c"},"a":{"$ref":"#/x-a"}},"x-a":{"$id":"http://x.test/c","type":"string"}}""")]
    public void The_order_of_properties_does_not_decide_what_a_reference_finds(string document) =>
        Assert.False(Z.FromJsonSchema(document).SafeParse("""{"b":1}""").IsSuccess);

    [Fact]
    public void A_fragment_on_the_base_around_it_starts_no_resource()
    {
        const string Document = """{"$schema":"http://json-schema.org/draft-07/schema#","$id":"http://x.test/root","definitions":{"a":{"$id":"http://x.test/root#foo","type":"string"}},"properties":{"p":{"$ref":"#foo"}}}""";

        Assert.False(Z.FromJsonSchema(Document).SafeParse("""{"p":1}""").IsSuccess);
    }

    [Theory]
    [InlineData("""{"$defs":{"d":{"$ref":"#/$defs/e","unevaluatedProperties":false},"e":{"properties":{"a":true}}},"not":{"$ref":"#/$defs/d"}}""", "pattern", """{"b":1}""")]
    [InlineData("""{"$defs":{"d":{"allOf":[{"properties":{"a":true}}],"unevaluatedProperties":false}},"if":{"$ref":"#/$defs/d"},"then":false}""", "pattern", """{"b":1}""")]
    [InlineData("""{"$defs":{"d":{"allOf":[{"properties":{"a":true}}],"unevaluatedProperties":false}},"title":"t","oneOf":[{"$ref":"#/$defs/d"},{"required":["b"]}]}""", "pattern", """{"b":1}""")]
    [InlineData("""{"contains":{"type":"string","pattern":"^a"},"maxContains":2,"unevaluatedItems":false}""", "pattern", """["a"]""")]
    [InlineData("""{"contains":{"minimum":1},"maxContains":1,"unevaluatedItems":false}""", "minimum", "[1]")]
    public void Losing_unevaluated_counts_as_loosening(string document, string keyword, string instance)
    {
        JsonValueSchema schema = Z.FromJsonSchema(document);
        string text = schema.ToJsonSchema(new JsonSchemaOptions { Target = Relocating(keyword) }).ToJsonString();

        Assert.True(schema.SafeParse(instance).IsSuccess);
        Assert.True(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft202012, instance), text);
    }

    [Theory]
    [InlineData("""{"$defs":{"t":{"properties":{"p":{"$ref":"#/$defs/s"}}},"s":{"$ref":"#/$defs/v","unevaluatedProperties":false},"v":{"allOf":[{"$ref":"#/$defs/t"}]}},"$ref":"#/$defs/t"}""", """{"p":{"p":{}}}""")]
    [InlineData("""{"$defs":{"t":{"prefixItems":[{"$ref":"#/$defs/s"}]},"s":{"$ref":"#/$defs/v","unevaluatedItems":false},"v":{"allOf":[{"$ref":"#/$defs/t"}]}},"$ref":"#/$defs/t"}""", "[[[]]]")]
    public void A_rejection_inside_a_target_takes_unevaluated_beside_the_reference(string document, string instance)
    {
        JsonValueSchema schema = Z.FromJsonSchema(document);
        JsonSchemaProfile profile = JsonSchemaProfile.Draft202012.With(Rewriters.InlineOrRejectRecursion);
        string text = schema.ToJsonSchema(new JsonSchemaOptions { Target = profile }).ToJsonString();

        Assert.True(schema.SafeParse(instance).IsSuccess);
        Assert.True(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft202012, instance), text);
    }

    private static string Tree(int depth) => depth == 0 ? "{}" : "{\"l\":" + Tree(depth - 1) + ",\"r\":" + Tree(depth - 1) + "}";

    [Fact]
    public void A_tree_of_ordinary_size_passes_under_unevaluated()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""{"$defs":{"n":{"anyOf":[{"properties":{"l":{"$ref":"#/$defs/n"},"r":{"$ref":"#/$defs/n"}}},{"title":"x"}],"unevaluatedProperties":false}},"$ref":"#/$defs/n"}""");

        Assert.True(schema.SafeParse(Tree(13)).IsSuccess);
    }

    [Fact]
    public void An_id_may_look_like_a_file()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""{"$defs":{"a":{"$id":"document.json","type":"string"}},"properties":{"p":{"$ref":"document.json"}}}""");

        Assert.False(schema.SafeParse("""{"p":1}""").IsSuccess);
    }

    [Fact]
    public void A_fragment_on_the_base_is_written_as_a_name()
    {
        const string Document = """{"$schema":"http://json-schema.org/draft-07/schema#","$id":"http://x.test/root","definitions":{"a":{"$id":"http://x.test/root#foo","type":"string"}},"properties":{"p":{"$ref":"#foo"}}}""";
        string text = Z.FromJsonSchema(Document).ToJsonSchema().ToJsonString();

        Assert.DoesNotContain("root-2", text, StringComparison.Ordinal);
        Assert.False(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft202012, """{"p":1}"""), text);
    }

    [Fact]
    public void A_pointer_fragment_on_the_base_starts_no_resource()
    {
        const string Document = """{"$schema":"http://json-schema.org/draft-07/schema#","$id":"http://x.test/root.json","definitions":{"a":{"$id":"http://x.test/root.json#/definitions/a","properties":{"p":{"$ref":"#/definitions/b"}}},"b":{"type":"string"}},"properties":{"q":{"$ref":"#/definitions/a"}}}""";
        JsonValueSchema schema = Z.FromJsonSchema(Document);

        Assert.True(schema.SafeParse("""{"q":{"p":"s"}}""").IsSuccess);
        Assert.False(schema.SafeParse("""{"q":{"p":1}}""").IsSuccess);
    }

    [Fact]
    public void Fragments_on_another_base_still_share_one_resource_only_once()
    {
        const string Document = """{"$schema":"http://json-schema.org/draft-07/schema#","definitions":{"a":{"$id":"http://o.test/x#a"},"b":{"$id":"http://o.test/x#b"}}}""";

        Assert.Equal(JsonSchemaImportCodes.InvalidSchema, Assert.Throws<JsonSchemaImportException>(() => Z.FromJsonSchema(Document)).Code);
    }

    [Fact]
    public void A_small_stack_fails_the_parse_and_keeps_the_process()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""{"items":{"$ref":"#"}}""");
        System.Text.Json.JsonElement deep = System.Text.Json.JsonElement.Parse(new string('[', 170) + new string(']', 170), new System.Text.Json.JsonDocumentOptions { MaxDepth = 400 });
        bool finished = false;

        System.Threading.Thread thread = new(() =>
        {
            _ = schema.SafeParse(deep);
            finished = true;
        }, 256 * 1024);

        thread.Start();
        thread.Join();

        Assert.True(finished);
    }
}
