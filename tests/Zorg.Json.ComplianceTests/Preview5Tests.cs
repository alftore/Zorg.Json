using System;
using System.Collections.Generic;
using Xunit;
using Zorg.Json.Emission;
using Zorg.Json.Import;

namespace Zorg.Json.ComplianceTests;

/// <summary>References between documents, and <c>$dynamicRef</c>, beyond what the official suite covers.</summary>
public sealed class Preview5Tests
{
    private static JsonSchemaImportOptions With(params (string Uri, string Text)[] documents)
    {
        Dictionary<string, string> all = [];

        foreach ((string uri, string text) in documents)
        {
            all[uri] = text;
        }

        return new JsonSchemaImportOptions { Documents = all };
    }

    [Fact]
    public void A_reference_reaches_another_document_and_the_result_stands_alone()
    {
        JsonValueSchema schema = Z.FromJsonSchema(
            """{"properties":{"p":{"$ref":"http://e.com/a.json#/$defs/b"}}}""",
            With(("http://e.com/a.json", """{"$defs":{"b":{"type":"string"}}}""")));

        Assert.True(schema.SafeParse("""{"p":"x"}""").IsSuccess);
        Assert.False(schema.SafeParse("""{"p":1}""").IsSuccess);

        string text = schema.ToJsonSchema().ToJsonString();
        Assert.False(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft202012, """{"p":1}"""), text);
        Assert.False(Z.FromJsonSchema(text).SafeParse("""{"p":1}""").IsSuccess);
    }

    [Fact]
    public void Two_documents_can_refer_to_each_other()
    {
        JsonValueSchema schema = Z.FromJsonSchema(
            """{"$ref":"http://e.com/a.json"}""",
            With(
                ("http://e.com/a.json", """{"properties":{"b":{"$ref":"b.json"},"v":{"type":"string"}}}"""),
                ("http://e.com/b.json", """{"properties":{"a":{"$ref":"a.json"}}}""")));

        Assert.True(schema.SafeParse("""{"b":{"a":{"v":"x"}}}""").IsSuccess);
        Assert.False(schema.SafeParse("""{"b":{"a":{"v":1}}}""").IsSuccess);

        foreach (JsonSchemaProfile profile in new[] { JsonSchemaProfile.Draft202012, JsonSchemaProfile.Draft07, JsonSchemaProfile.OpenApi30 })
        {
            string text = schema.ToJsonSchema(new JsonSchemaOptions { Target = profile }).ToJsonString();

            Assert.False(EmissionReviewTests.Accepts(text, profile, """{"b":{"a":{"v":1}}}"""), text);
        }
    }

    [Fact]
    public void A_document_with_its_own_id_answers_to_both_uris()
    {
        JsonValueSchema schema = Z.FromJsonSchema(
            """{"properties":{"p":{"$ref":"http://e.com/given.json"},"q":{"$ref":"http://e.com/own.json"}}}""",
            With(("http://e.com/given.json", """{"$id":"http://e.com/own.json","type":"string"}""")));

        Assert.False(schema.SafeParse("""{"p":1}""").IsSuccess);
        Assert.False(schema.SafeParse("""{"q":1}""").IsSuccess);
    }

    [Fact]
    public void A_name_already_in_the_definitions_is_kept()
    {
        JsonValueSchema schema = Z.FromJsonSchema(
            """{"$defs":{"document-1":{"type":"integer"}},"properties":{"p":{"$ref":"#/$defs/document-1"},"q":{"$ref":"http://e.com/a.json"}}}""",
            With(("http://e.com/a.json", """{"type":"string"}""")));

        Assert.True(schema.SafeParse("""{"p":1,"q":"x"}""").IsSuccess);
    }

    [Fact]
    public void Text_and_numbers_of_the_schema_are_kept()
    {
        JsonValueSchema schema = Z.FromJsonSchema(
            """{"properties":{"n":{"const":100000000000000000000000000001},"s":{"const":"é"},"p":{"$ref":"http://e.com/a.json"}}}""",
            With(("http://e.com/a.json", "{}")));

        Assert.True(schema.SafeParse("""{"n":100000000000000000000000000001,"s":"é"}""").IsSuccess);
        Assert.False(schema.SafeParse("""{"n":100000000000000000000000000000}""").IsSuccess);
    }

    [Theory]
    [InlineData("relative.json", "{}", JsonSchemaImportCodes.InvalidSchema)]
    [InlineData("http://e.com/a#frag", "{}", JsonSchemaImportCodes.InvalidSchema)]
    [InlineData("http://e.com/a", "{", JsonSchemaImportCodes.InvalidSchema)]
    [InlineData("http://e.com/a", """{"$schema":"http://json-schema.org/draft-07/schema#"}""", JsonSchemaImportCodes.UnsupportedDialect)]
    public void A_document_that_can_not_be_read_is_a_refusal(string name, string text, string code)
    {
        JsonSchemaImportException exception = Assert.Throws<JsonSchemaImportException>(() => Z.FromJsonSchema("{}", With((name, text))));

        Assert.Equal(code, exception.Code);
    }

    [Fact]
    public void A_boolean_schema_has_no_room_for_documents()
    {
        Assert.Throws<JsonSchemaImportException>(() => Z.FromJsonSchema("true", With(("http://e.com/a", "{}"))));
    }

    private const string Tree = """
        {"$id":"http://e.com/tree","$dynamicAnchor":"node","type":"object","properties":{"child":{"$dynamicRef":"#node"},"v":{}}}
        """;

    private const string StrictTree = """
        {"$id":"http://e.com/strict","$dynamicAnchor":"node","$ref":"http://e.com/tree","unevaluatedProperties":false}
        """;

    [Fact]
    public void A_dynamic_reference_lands_in_the_outermost_resource()
    {
        JsonValueSchema schema = Z.FromJsonSchema(StrictTree, With(("http://e.com/tree", Tree)));

        Assert.True(schema.SafeParse("""{"child":{"v":1}}""").IsSuccess);
        Assert.False(schema.SafeParse("""{"child":{"junk":1}}""").IsSuccess);
        Assert.True(Z.FromJsonSchema(Tree).SafeParse("""{"child":{"junk":1}}""").IsSuccess);
    }

    [Fact]
    public void A_dynamic_reference_means_the_same_when_written()
    {
        JsonValueSchema schema = Z.FromJsonSchema(StrictTree, With(("http://e.com/tree", Tree)));
        string text = schema.ToJsonSchema().ToJsonString();

        Assert.True(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft202012, """{"child":{"v":1}}"""), text);
        Assert.False(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft202012, """{"child":{"junk":1}}"""), text);
    }

    [Fact]
    public void A_dialect_without_the_keyword_leaves_it_out_and_is_only_looser()
    {
        JsonValueSchema schema = Z.FromJsonSchema(StrictTree, With(("http://e.com/tree", Tree)));

        foreach (JsonSchemaProfile profile in new[] { JsonSchemaProfile.Draft07, JsonSchemaProfile.OpenApi30 })
        {
            EmitResult result = schema.Emit(new JsonSchemaOptions { Target = profile });
            string text = result.Schema.ToJsonString();

            Assert.DoesNotContain("\"$dynamicRef\":", text, StringComparison.Ordinal);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Keyword == "$dynamicRef");
            Assert.True(EmissionReviewTests.Accepts(text, profile, """{"child":{"v":1}}"""), text);
        }
    }

    [Theory]
    [InlineData("""{"$defs":{"a":{"$dynamicAnchor":"n","type":"string"}},"not":{"$dynamicRef":"#n"}}""", "1")]
    [InlineData("""{"$defs":{"a":{"$dynamicAnchor":"n","type":"string"}},"oneOf":[{"$dynamicRef":"#n"},{"type":"string"}]}""", "1")]
    public void Leaving_it_out_under_not_or_one_of_is_handled_like_any_inexact_rule(string document, string instance)
    {
        JsonValueSchema schema = Z.FromJsonSchema(document);

        foreach (JsonSchemaProfile profile in new[] { JsonSchemaProfile.Draft07, JsonSchemaProfile.OpenApi30 })
        {
            string text = schema.ToJsonSchema(new JsonSchemaOptions { Target = profile }).ToJsonString();

            if (schema.SafeParse(instance).IsSuccess)
            {
                Assert.True(EmissionReviewTests.Accepts(text, profile, instance), text);
            }
        }
    }

    [Fact]
    public void A_reference_prefix_leaves_it_out_and_takes_unevaluated_with_it()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""{"$defs":{"a":{"$dynamicAnchor":"n","properties":{"x":{}}}},"$dynamicRef":"#n","unevaluatedProperties":false}""");
        string text = schema.Emit(new JsonSchemaOptions { ReferencePrefix = "#/components/schemas/" }).Schema.ToJsonString();

        Assert.True(schema.SafeParse("""{"x":1}""").IsSuccess);
        Assert.DoesNotContain("\"$dynamicRef\":", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"unevaluatedProperties\":", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Beside_a_reference_it_is_refused()
    {
        JsonSchemaImportException exception = Assert.Throws<JsonSchemaImportException>(
            () => Z.FromJsonSchema("""{"$defs":{"a":{"$dynamicAnchor":"n"}},"$ref":"#/$defs/a","$dynamicRef":"#n"}"""));

        Assert.Equal(JsonSchemaImportCodes.UnsupportedKeyword, exception.Code);
    }

    [Fact]
    public void A_cycle_that_never_reaches_a_child_is_refused()
    {
        JsonSchemaImportException exception = Assert.Throws<JsonSchemaImportException>(() => Z.FromJsonSchema("""{"$dynamicAnchor":"n","$dynamicRef":"#n"}"""));

        Assert.Equal(JsonSchemaImportCodes.RefCycle, exception.Code);
    }

    [Fact]
    public void A_cycle_only_the_dynamic_scope_can_make_ends_within_the_limits()
    {
        JsonValueSchema schema = Z.FromJsonSchema(
            """{"$id":"http://e.com/o","$dynamicAnchor":"n","$ref":"http://e.com/i"}""",
            With(("http://e.com/i", """{"$dynamicAnchor":"n","allOf":[{"$dynamicRef":"#n"}]}""")));

        Assert.False(schema.SafeParse("1").IsSuccess);
    }

    [Fact]
    public void A_parse_that_fails_leaves_nothing_for_the_next()
    {
        JsonValueSchema schema = Z.FromJsonSchema(StrictTree, With(("http://e.com/tree", Tree)));

        for (int round = 0; round < 3; round++)
        {
            Assert.False(schema.SafeParse("""{"child":{"junk":1}}""").IsSuccess);
            Assert.True(schema.SafeParse("""{"child":{}}""").IsSuccess);
        }
    }

    [Fact]
    public void In_older_drafts_the_keywords_mean_nothing()
    {
        const string Document = """{"$schema":"http://json-schema.org/draft-07/schema#","$dynamicRef":"#missing","type":"string"}""";

        Assert.True(Z.FromJsonSchema(Document).SafeParse("\"x\"").IsSuccess);
    }
}
