using System;
using System.Linq;
using System.Text.Json;
using Xunit;
using Zorg.Json.Import;
using Zorg.Json.Validation;

namespace Zorg.Json.Tests;

/// <summary>Cases from the review of the importer, each of which once went wrong.</summary>
public sealed class ImportReviewTests
{
    private static void AssertInvalid(Action import, string? location = null)
    {
        JsonSchemaImportException exception = Assert.Throws<JsonSchemaImportException>(import);
        Assert.Equal(JsonSchemaImportCodes.InvalidSchema, exception.Code);

        if (location is not null)
        {
            Assert.Equal(location, exception.SchemaLocation);
        }
    }

    [Theory]
    [InlineData("""{ "title": "\ud800" }""", "/title")]
    [InlineData("""{ "\ud800": 1 }""", "")]
    [InlineData("""{ "$schema": "\ud800" }""", "/$schema")]
    [InlineData("""{ "$ref": "#\ud800" }""", "/$ref")]
    [InlineData("""{ "required": ["\ud800"] }""", "/required/0")]
    [InlineData("""{ "properties": { "\ud800": {} } }""", "/properties")]
    [InlineData("""{ "type": "\udc00" }""", "/type")]
    public void Text_that_cannot_be_decoded_is_an_invalid_schema(string schema, string location)
    {
        // Text with an undecodable property name already fails while it is parsed, before there is a location.
        AssertInvalid(() => Z.FromJsonSchema(schema));
        AssertInvalid(() => Z.FromJsonSchema(JsonElement.Parse(schema)), location);
    }

    [Fact]
    public void A_lone_surrogate_in_the_text_is_an_invalid_schema()
    {
        string schema = "{\"title\":\"" + '\ud800' + "\"}";
        AssertInvalid(() => Z.FromJsonSchema(schema));
    }

    [Fact]
    public void Values_that_are_only_kept_are_not_decoded()
    {
        // The sample generator relies on these importing. Nothing can equal them, since instances can't hold a lone surrogate.
        JsonValueSchema schema = Z.FromJsonSchema("""{ "const": "\ud800", "x-note": "\udc00" }""");
        Assert.False(schema.SafeParse("\"a\"").IsSuccess);
    }

    [Fact]
    public void A_surrogate_pair_is_still_text()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""{ "title": "😀", "const": "😀" }""");
        Assert.True(schema.SafeParse("\"\\ud83d\\ude00\"").IsSuccess);
    }

    [Theory]
    [InlineData("draft7")]
    [InlineData("draft4")]
    public void An_empty_array_form_of_items_is_an_invalid_schema(string draft)
    {
        string dialect = $"http://json-schema.org/{(draft == "draft7" ? "draft-07" : "draft-04")}/schema#";
        AssertInvalid(() => Z.FromJsonSchema($$"""{ "$schema": "{{dialect}}", "items": [] }"""), "/items");
        AssertInvalid(() => Z.FromJsonSchema($$"""{ "$schema": "{{dialect}}", "properties": { "a": { "items": [], "additionalItems": false } } }"""), "/properties/a/items");
    }

    [Fact]
    public void An_empty_array_form_of_items_is_refused_in_draft_06_too() =>
        AssertInvalid(() => Z.FromJsonSchema("""{ "$schema": "http://json-schema.org/draft-06/schema#", "items": [] }"""), "/items");

    [Fact]
    public void An_empty_prefix_items_is_still_refused() =>
        AssertInvalid(() => Z.FromJsonSchema("""{ "prefixItems": [] }"""), "/prefixItems");

    [Fact]
    public void Items_is_not_checked_where_the_dialect_ignores_it()
    {
        // Beside $ref, draft-07 ignores items, so it is never read as a schema.
        JsonValueSchema schema = Z.FromJsonSchema("""{ "$schema": "http://json-schema.org/draft-07/schema#", "$ref": "#/definitions/a", "items": [], "definitions": { "a": { "type": "array" } } }""");
        Assert.True(schema.SafeParse("[1]").IsSuccess);
    }

    [Fact]
    public void Compiled_patterns_are_shared_by_text()
    {
        string pattern = "^review-shared-" + Guid.NewGuid().ToString("N") + @"\p{L}+$";
        string text = JsonSerializer.Serialize(pattern);
        _ = Z.FromJsonSchema($$"""{ "pattern": {{text}}, "patternProperties": { {{text}}: true } }""");

        Assert.Same(PatternCompiler.Compile(pattern), PatternCompiler.Compile(pattern));
    }

    [Fact]
    public void A_pattern_that_failed_to_compile_is_not_remembered()
    {
        Assert.ThrowsAny<ArgumentException>(() => PatternCompiler.Compile("(unclosed"));
        Assert.ThrowsAny<ArgumentException>(() => PatternCompiler.Compile("(unclosed"));
    }

    [Fact]
    public void The_pattern_cache_does_not_grow_without_bound()
    {
        for (int index = 0; index < 5_000; index++)
        {
            _ = PatternCompiler.Compile($"^bounded-{index}$");
        }

        Assert.True(PatternCompiler.CachedCount <= 1_024, $"{PatternCompiler.CachedCount} entries");
    }

    private static string DistinctPatterns(int count, int length) =>
        "{ \"allOf\": [" + string.Join(',', Enumerable.Range(0, count).Select(index =>
            "{ \"pattern\": \"" + $"^{index}-".PadRight(length, 'a') + "\" }")) + "] }";

    [Fact]
    public void The_patterns_of_a_document_share_a_budget()
    {
        JsonSchemaImportOptions options = new() { MaxTotalPatternLength = 1_000 };

        _ = Z.FromJsonSchema(DistinctPatterns(10, 100), options);

        JsonSchemaImportException exception = Assert.Throws<JsonSchemaImportException>(() => Z.FromJsonSchema(DistinctPatterns(11, 100), options));
        Assert.Equal(JsonSchemaImportCodes.LimitExceeded, exception.Code);
        Assert.Equal("/allOf/10/pattern", exception.SchemaLocation);
        Assert.Contains("MaxTotalPatternLength", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_repeated_pattern_is_counted_once()
    {
        string schema = "{ \"allOf\": [" + string.Join(',', Enumerable.Repeat("{ \"pattern\": \"^" + new string('a', 98) + "$\" }", 50)) + "] }";
        _ = Z.FromJsonSchema(schema, new JsonSchemaImportOptions { MaxTotalPatternLength = 100 });
    }

    [Fact]
    public void Pattern_property_names_count_towards_the_budget()
    {
        string names = string.Join(',', Enumerable.Range(0, 11).Select(index => "\"" + $"^{index}-".PadRight(100, 'a') + "\": true"));
        JsonSchemaImportException exception = Assert.Throws<JsonSchemaImportException>(
            () => Z.FromJsonSchema("{ \"patternProperties\": {" + names + "} }", new JsonSchemaImportOptions { MaxTotalPatternLength = 1_000 }));

        Assert.Equal(JsonSchemaImportCodes.LimitExceeded, exception.Code);
    }

    [Fact]
    public void A_resource_off_the_walked_tree_can_refer_to_its_own_root()
    {
        // This once ended the process with a stack overflow: the root of the resource got a home inside itself.
        const string Document = """
            {
              "$schema": "http://json-schema.org/draft-07/schema#",
              "additionalItems": { "$id": "http://example.com/in", "properties": { "b": { "$ref": "#" } }, "required": ["a"] },
              "properties": { "x": { "$ref": "#/additionalItems" } }
            }
            """;

        JsonValueSchema schema = Z.FromJsonSchema(Document);

        Assert.True(schema.SafeParse("""{"x":{"a":1,"b":{"a":2}}}""").IsSuccess);
        Assert.False(schema.SafeParse("""{"x":{"a":1,"b":{}}}""").IsSuccess);
    }

    [Fact]
    public void The_default_budget_is_set() =>
        Assert.Equal(65_536, new JsonSchemaImportOptions().MaxTotalPatternLength);
}
