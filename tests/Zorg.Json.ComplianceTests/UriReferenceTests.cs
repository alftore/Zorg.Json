using System;
using System.Text.Json.Nodes;
using Xunit;
using Zorg.Json.Emission;
using Zorg.Json.Import;

namespace Zorg.Json.ComplianceTests;

/// <summary>A <c>$ref</c> that names a resource of the same document by URI. Nothing is fetched, and the written schema must mean the same.</summary>
public sealed class UriReferenceTests
{
    private static readonly JsonSchemaProfile[] Profiles =
        [JsonSchemaProfile.Draft202012, JsonSchemaProfile.Draft07, JsonSchemaProfile.OpenApi31, JsonSchemaProfile.OpenApi30];

    public static TheoryData<string, string, string> Documents { get; } = new()
    {
        // Relative, against the document and against a nearer $id.
        { """{"$defs":{"a":{"$id":"a.json","type":"string"}},"properties":{"p":{"$ref":"a.json"}}}""", """{"p":"x"}""", """{"p":1}""" },
        { """{"$id":"http://e.com/dir/root.json","$defs":{"a":{"$id":"a.json","type":"string"}},"properties":{"p":{"$ref":"a.json"}}}""", """{"p":"x"}""", """{"p":1}""" },
        { """{"$id":"http://e.com/dir/root.json","$defs":{"a":{"$id":"sub/","$defs":{"b":{"$id":"b.json","type":"string"}}}},"properties":{"p":{"$ref":"sub/b.json"}}}""", """{"p":"x"}""", """{"p":1}""" },
        { """{"$id":"http://e.com/dir/root.json","$defs":{"a":{"$id":"/top.json","type":"string"}},"properties":{"p":{"$ref":"http://e.com/top.json"}}}""", """{"p":"x"}""", """{"p":1}""" },

        // Absolute and URN, with a pointer or an anchor.
        { """{"$defs":{"a":{"$id":"http://e.com/a","$defs":{"b":{"type":"string"}}}},"properties":{"p":{"$ref":"http://e.com/a#/$defs/b"}}}""", """{"p":"x"}""", """{"p":1}""" },
        { """{"$defs":{"a":{"$id":"http://e.com/a","$defs":{"b":{"$anchor":"n","type":"string"}}}},"properties":{"p":{"$ref":"http://e.com/a#n"}}}""", """{"p":"x"}""", """{"p":1}""" },
        { """{"$defs":{"a":{"$id":"urn:uuid:deadbeef-1234-ffff-ffff-4321feebdaed","type":"string"}},"properties":{"p":{"$ref":"urn:uuid:deadbeef-1234-ffff-ffff-4321feebdaed"}}}""", """{"p":"x"}""", """{"p":1}""" },

        // The root by its own $id, and a resource that points back at it.
        { """{"$id":"http://e.com/r","properties":{"n":{"$ref":"http://e.com/r"},"v":{"type":"string"}}}""", """{"n":{"v":"x"}}""", """{"n":{"v":1}}""" },
        { """{"$id":"http://e.com/r","$defs":{"a":{"$id":"a","properties":{"up":{"$ref":"r"}}}},"properties":{"down":{"$ref":"a"},"v":{"type":"string"}}}""", """{"down":{"up":{"v":"x"}}}""", """{"down":{"up":{"v":1}}}""" },

        // Two resources that refer to each other.
        { """{"$defs":{"a":{"$id":"http://e.com/a","properties":{"b":{"$ref":"b"},"v":{"type":"string"}}},"b":{"$id":"http://e.com/b","properties":{"a":{"$ref":"a"}}}},"$ref":"http://e.com/a"}""", """{"b":{"a":{"v":"x"}}}""", """{"b":{"a":{"v":1}}}""" },
    };

    [Theory]
    [MemberData(nameof(Documents))]
    public void A_reference_by_uri_finds_its_resource_and_means_the_same_when_written(string document, string good, string bad)
    {
        JsonValueSchema schema = Z.FromJsonSchema(document);

        Assert.True(schema.SafeParse(good).IsSuccess);
        Assert.False(schema.SafeParse(bad).IsSuccess);

        foreach (JsonSchemaProfile profile in Profiles)
        {
            string text = schema.ToJsonSchema(new JsonSchemaOptions { Target = profile }).ToJsonString();

            Assert.True(EmissionReviewTests.Accepts(text, profile, good), $"{profile.Dialect}: {text}");
            Assert.False(EmissionReviewTests.Accepts(text, profile, bad), $"{profile.Dialect}: {text}");

            JsonValueSchema again = Z.FromJsonSchema(text);
            Assert.True(again.SafeParse(good).IsSuccess, text);
            Assert.False(again.SafeParse(bad).IsSuccess, text);
        }
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void A_reference_prefix_keeps_the_meaning_and_writes_no_uri(string document, string good, string bad)
    {
        _ = good;
        _ = bad;
        EmitResult result = Z.FromJsonSchema(document).Emit(new JsonSchemaOptions { ReferencePrefix = "#/components/schemas/" });
        JsonObject all = new() { ["root"] = result.Schema.DeepClone() };

        foreach ((string name, JsonNode definition) in result.Definitions)
        {
            all[name] = definition.DeepClone();
        }

        string text = all.ToJsonString();

        Assert.DoesNotContain("\"$id\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"$ref\":\"http", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"$ref\":\"a", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_written_document_keeps_the_text_of_its_references()
    {
        const string Document = """{"$schema":"https://json-schema.org/draft/2020-12/schema","$defs":{"a":{"$id":"http://e.com/a","$defs":{"b":{"type":"string"}}}},"properties":{"p":{"$ref":"http://e.com/a#/$defs/b"}}}""";

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Document), Z.FromJsonSchema(Document).ToJsonSchema()));
    }

    [Fact]
    public void A_fragment_follows_a_keyword_that_was_respelled()
    {
        const string Document = """{"$schema":"http://json-schema.org/draft-07/schema#","definitions":{"a":{"$id":"http://e.com/a","definitions":{"b":{"type":"string"}}}},"properties":{"p":{"$ref":"http://e.com/a#/definitions/b"}}}""";
        string text = Z.FromJsonSchema(Document).ToJsonSchema().ToJsonString();

        Assert.Contains("\"$ref\":\"http://e.com/a#/$defs/b\"", text, StringComparison.Ordinal);
        Assert.False(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft202012, """{"p":1}"""), text);
    }

    [Fact]
    public void An_import_inside_a_fluent_schema_keeps_working()
    {
        JsonValueSchema imported = Z.FromJsonSchema("""{"$defs":{"a":{"$id":"http://e.com/a","type":"string"}},"properties":{"p":{"$ref":"http://e.com/a"}}}""");
        ObjectSchema outer = Z.Object().Property("x", imported);
        string text = outer.ToJsonSchema().ToJsonString();

        Assert.True(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft202012, """{"x":{"p":"s"}}"""), text);
        Assert.False(EmissionReviewTests.Accepts(text, JsonSchemaProfile.Draft202012, """{"x":{"p":1}}"""), text);
    }

    [Theory]
    [InlineData("""{"$ref":"http://e.com/elsewhere.json"}""")]
    [InlineData("""{"$ref":"elsewhere.json#/$defs/a"}""")]
    [InlineData("""{"$ref":"https://json-schema.org/draft/2020-12/schema"}""")]
    [InlineData("""{"$defs":{"a":{"$id":"http://e.com/a"}},"$ref":"http://e.com/a#/$defs/missing"}""")]
    [InlineData("""{"$defs":{"a":{"$id":"http://e.com/a"}},"$ref":"http://e.com/a#missing"}""")]
    public void What_is_not_in_the_document_stays_unresolved(string document)
    {
        JsonSchemaImportException exception = Assert.Throws<JsonSchemaImportException>(() => Z.FromJsonSchema(document));

        Assert.Equal(JsonSchemaImportCodes.UnresolvedRef, exception.Code);
    }

    [Fact]
    public void An_id_under_an_unknown_keyword_or_in_an_enum_is_no_resource()
    {
        Assert.Throws<JsonSchemaImportException>(() => Z.FromJsonSchema("""{"x-other":{"$id":"http://e.com/a"},"$ref":"http://e.com/a"}"""));
        Assert.Throws<JsonSchemaImportException>(() => Z.FromJsonSchema("""{"$defs":{"e":{"enum":[{"$id":"http://e.com/a"}]}},"$ref":"http://e.com/a"}"""));
    }

    [Fact]
    public void A_sibling_id_of_a_reference_changes_nothing_before_2019()
    {
        const string Document = """{"$schema":"http://json-schema.org/draft-07/schema#","$id":"http://e.com/root","definitions":{"a":{"$id":"http://e.com/a","type":"string"}},"properties":{"p":{"$id":"http://e.com/other","$ref":"a"}}}""";
        JsonValueSchema schema = Z.FromJsonSchema(Document);

        Assert.False(schema.SafeParse("""{"p":1}""").IsSuccess);
        Assert.True(schema.SafeParse("""{"p":"x"}""").IsSuccess);
    }

    [Fact]
    public void Text_that_is_no_uri_does_not_throw_anything_else()
    {
        foreach (string text in new[] { "http://[bad", "%", "a b", "\\\\x", "http://e.com:notaport/" })
        {
            string document = $$"""{"$id":"{{text.Replace("\\", "\\\\", StringComparison.Ordinal)}}","$ref":"{{text.Replace("\\", "\\\\", StringComparison.Ordinal)}}x"}""";

            try
            {
                _ = Z.FromJsonSchema(document);
            }
            catch (JsonSchemaImportException)
            {
                // A refusal is fine. Any other exception fails this test.
            }
        }
    }
}
