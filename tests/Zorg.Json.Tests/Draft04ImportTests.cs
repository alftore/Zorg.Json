using System;
using System.Linq;
using Xunit;
using Zorg.Json.Emission;
using Zorg.Json.Import;

namespace Zorg.Json.Tests;

public sealed class Draft04ImportTests
{
    private const string Dialect = "\"$schema\": \"http://json-schema.org/draft-04/schema#\"";

    private static readonly JsonSchemaImportOptions Options = new() { DefaultDialect = JsonSchemaImportDialect.Draft04 };

    private static JsonValueSchema Import(string schema) => Z.FromJsonSchema(schema, Options);

    private static string Emit(string schema) => Import(schema).ToJsonSchema().ToJsonString();

    private static void AssertInvalid(string schema, string location)
    {
        JsonSchemaImportException exception = Assert.Throws<JsonSchemaImportException>(() => Import(schema));
        Assert.Equal(JsonSchemaImportCodes.InvalidSchema, exception.Code);
        Assert.Equal(location, exception.SchemaLocation);
    }

    [Theory]
    [InlineData("http://json-schema.org/draft-04/schema#")]
    [InlineData("http://json-schema.org/draft-04/schema")]
    public void The_dialect_is_detected(string dialect)
    {
        JsonValueSchema schema = Z.FromJsonSchema($$"""{ "$schema": "{{dialect}}", "minimum": 1, "exclusiveMinimum": true }""");

        Assert.False(schema.SafeParse("1").IsSuccess);
        Assert.True(schema.SafeParse("2").IsSuccess);
    }

    [Fact]
    public void A_subschema_may_repeat_the_dialect() =>
        Assert.True(Z.FromJsonSchema($$"""{ {{Dialect}}, "definitions": { "a": { {{Dialect}} } } }""").SafeParse("1").IsSuccess);

    [Theory]
    [InlineData("""{ "minimum": 1, "exclusiveMinimum": true }""", "1", false)]
    [InlineData("""{ "minimum": 1, "exclusiveMinimum": true }""", "1.1", true)]
    [InlineData("""{ "minimum": 1, "exclusiveMinimum": false }""", "1", true)]
    [InlineData("""{ "minimum": 1, "exclusiveMinimum": false }""", "0.9", false)]
    [InlineData("""{ "maximum": 1, "exclusiveMaximum": true }""", "1", false)]
    [InlineData("""{ "maximum": 1, "exclusiveMaximum": true }""", "0.9", true)]
    [InlineData("""{ "maximum": 1, "exclusiveMaximum": false }""", "1", true)]
    public void Exclusive_booleans_change_the_limit(string schema, string value, bool valid)
    {
        Assert.Equal(valid, Import(schema).SafeParse(value).IsSuccess);
        Assert.Equal(valid, Z.FromJsonSchema(Emit(schema)).SafeParse(value).IsSuccess);
    }

    [Fact]
    public void Exclusive_limits_emit_as_numbers() =>
        Assert.Contains("\"exclusiveMinimum\":1", Emit("""{ "minimum": 1, "exclusiveMinimum": true }"""), StringComparison.Ordinal);

    [Theory]
    [InlineData("""{ "exclusiveMinimum": 1 }""", "/exclusiveMinimum")]
    [InlineData("""{ "minimum": 1, "exclusiveMinimum": 1 }""", "/exclusiveMinimum")]
    [InlineData("""{ "exclusiveMaximum": 1 }""", "/exclusiveMaximum")]
    [InlineData("""{ "exclusiveMinimum": true }""", "/exclusiveMinimum")]
    [InlineData("""{ "exclusiveMaximum": false }""", "/exclusiveMaximum")]
    [InlineData("""{ "maximum": 1, "exclusiveMinimum": true }""", "/exclusiveMinimum")]
    public void Exclusive_keywords_need_a_boolean_and_their_limit(string schema, string location) =>
        AssertInvalid(schema, location);

    [Theory]
    [InlineData("true", "")]
    [InlineData("false", "")]
    [InlineData("""{ "properties": { "a": true } }""", "/properties/a")]
    [InlineData("""{ "patternProperties": { "a": false } }""", "/patternProperties/a")]
    [InlineData("""{ "definitions": { "a": true } }""", "/definitions/a")]
    [InlineData("""{ "items": true }""", "/items")]
    [InlineData("""{ "items": [false] }""", "/items/0")]
    [InlineData("""{ "not": true }""", "/not")]
    [InlineData("""{ "allOf": [true] }""", "/allOf/0")]
    [InlineData("""{ "anyOf": [false] }""", "/anyOf/0")]
    [InlineData("""{ "oneOf": [true] }""", "/oneOf/0")]
    [InlineData("""{ "dependencies": { "a": true } }""", "/dependencies/a")]
    [InlineData("""{ "additionalProperties": { "not": true } }""", "/additionalProperties/not")]
    [InlineData("""{ "$ref": "#/x-a", "x-a": true }""", "/x-a")]
    public void Boolean_schemas_are_invalid(string schema, string location) =>
        AssertInvalid(schema, location);

    [Fact]
    public void Additional_keywords_may_be_booleans()
    {
        Assert.False(Import("""{ "additionalProperties": false }""").SafeParse("""{ "a": 1 }""").IsSuccess);
        Assert.True(Import("""{ "additionalProperties": true }""").SafeParse("""{ "a": 1 }""").IsSuccess);
        Assert.False(Import("""{ "items": [{}], "additionalItems": false }""").SafeParse("[1, 2]").IsSuccess);
        Assert.True(Import("""{ "items": [{}], "additionalItems": true }""").SafeParse("[1, 2]").IsSuccess);
    }

    [Theory]
    [InlineData("""{ "const": 1 }""", "x-inert-const")]
    [InlineData("""{ "contains": { "type": "string" } }""", "x-inert-contains")]
    [InlineData("""{ "propertyNames": { "maxLength": 0 } }""", "x-inert-propertyNames")]
    [InlineData("""{ "if": { "type": "string" }, "then": { "not": {} } }""", "x-inert-if")]
    [InlineData("""{ "$id": "#a" }""", "x-inert-$id")]
    [InlineData("""{ "$comment": 1 }""", "x-inert-$comment")]
    [InlineData("""{ "examples": 1 }""", "x-inert-examples")]
    [InlineData("""{ "deprecated": "no" }""", "x-inert-deprecated")]
    [InlineData("""{ "readOnly": "no" }""", "x-inert-readOnly")]
    [InlineData("""{ "dependentRequired": { "a": ["b"] } }""", "x-inert-dependentRequired")]
    public void Later_keywords_do_nothing(string schema, string inert)
    {
        Assert.True(Import(schema).SafeParse("""{ "a": [1] }""").IsSuccess);
        Assert.True(Import(schema).SafeParse("\"a\"").IsSuccess);
        Assert.Contains($"\"{inert}\"", Emit(schema), StringComparison.Ordinal);
        Assert.True(Z.FromJsonSchema(Emit(schema)).SafeParse("""{ "a": [1] }""").IsSuccess);
    }

    [Theory]
    [InlineData("""{ "contains": true, "const": false, "propertyNames": false }""")]
    [InlineData("""{ "if": false, "then": false, "else": false }""")]
    public void Later_keywords_may_hold_booleans(string schema) =>
        Assert.True(Import(schema).SafeParse("1").IsSuccess);

    [Fact]
    public void A_reference_into_a_later_keyword_survives_emitting()
    {
        const string Schema = """{ "properties": { "a": { "$ref": "#/contains/properties/b" } }, "contains": { "properties": { "b": { "type": "string" } } } }""";

        Assert.False(Import(Schema).SafeParse("""{ "a": 1 }""").IsSuccess);
        Assert.False(Z.FromJsonSchema(Emit(Schema)).SafeParse("""{ "a": 1 }""").IsSuccess);
    }

    [Fact]
    public void Id_names_a_location()
    {
        const string Schema = """{ "definitions": { "a": { "id": "#named", "type": "string" } }, "properties": { "b": { "$ref": "#named" } } }""";

        Assert.False(Import(Schema).SafeParse("""{ "b": 1 }""").IsSuccess);
        Assert.Contains("\"$anchor\":\"named\"", Emit(Schema), StringComparison.Ordinal);
        Assert.False(Z.FromJsonSchema(Emit(Schema)).SafeParse("""{ "b": 1 }""").IsSuccess);
    }

    [Fact]
    public void Id_emits_as_dollar_id() =>
        Assert.Contains("\"$id\":\"http://example.com/a\"", Emit("""{ "id": "http://example.com/a" }"""), StringComparison.Ordinal);

    [Fact]
    public void Dollar_id_names_nothing() =>
        Assert.Equal(
            JsonSchemaImportCodes.UnresolvedRef,
            Assert.Throws<JsonSchemaImportException>(() => Import("""{ "definitions": { "a": { "$id": "#named" } }, "$ref": "#named" }""")).Code);

    [Fact]
    public void Id_must_be_a_string() => AssertInvalid("""{ "id": 1 }""", "/id");

    [Fact]
    public void A_property_named_id_is_only_a_property() =>
        Assert.False(Import("""{ "properties": { "id": { "type": "string" } } }""").SafeParse("""{ "id": 1 }""").IsSuccess);

    [Fact]
    public void Siblings_of_a_reference_are_ignored()
    {
        const string Schema = """
            {
              "definitions": { "a": { "type": "number" } },
              "$ref": "#/definitions/a",
              "id": 1,
              "maximum": 0,
              "exclusiveMinimum": "no",
              "title": "kept"
            }
            """;

        Assert.True(Import(Schema).SafeParse("5").IsSuccess);
        Assert.True(Z.FromJsonSchema(Emit(Schema)).SafeParse("5").IsSuccess);
        Assert.Contains("\"x-inert-maximum\":0", Emit(Schema), StringComparison.Ordinal);
        Assert.Contains("\"title\":\"kept\"", Emit(Schema), StringComparison.Ordinal);
    }

    [Fact]
    public void An_ignored_id_isnt_an_unknown_keyword() =>
        Assert.True(Z.FromJsonSchema(
            """{ "definitions": { "a": {} }, "$ref": "#/definitions/a", "id": "b" }""",
            Options with { UnknownKeywords = UnknownKeywordHandling.Error }).SafeParse("1").IsSuccess);

    [Fact]
    public void Id_stays_unknown_in_other_dialects() =>
        Assert.Equal(
            JsonSchemaImportCodes.UnsupportedKeyword,
            Assert.Throws<JsonSchemaImportException>(() => Z.FromJsonSchema("""{ "id": "a" }""", new() { UnknownKeywords = UnknownKeywordHandling.Error })).Code);

    [Fact]
    public void Definitions_dependencies_and_tuples_are_translated()
    {
        const string Schema = """
            {
              "definitions": { "a": { "type": "string" } },
              "properties": { "x": { "$ref": "#/definitions/a" } },
              "dependencies": { "x": ["y"], "y": { "required": ["z"] } },
              "items": [{ "type": "string" }],
              "additionalItems": { "type": "number" }
            }
            """;

        foreach (JsonValueSchema schema in (JsonValueSchema[])[Import(Schema), Z.FromJsonSchema(Emit(Schema))])
        {
            Assert.True(schema.SafeParse("""{ "x": "a", "y": 1, "z": 1 }""").IsSuccess);
            Assert.False(schema.SafeParse("""{ "x": 1, "y": 1, "z": 1 }""").IsSuccess);
            Assert.False(schema.SafeParse("""{ "x": "a" }""").IsSuccess);
            Assert.False(schema.SafeParse("""{ "y": 1 }""").IsSuccess);
            Assert.True(schema.SafeParse("""["a", 1, 2]""").IsSuccess);
            Assert.False(schema.SafeParse("""["a", "b"]""").IsSuccess);
        }
    }

    [Theory]
    [InlineData("""{ "type": "integer" }""", "1", true)]
    [InlineData("""{ "type": "integer" }""", "-1", true)]
    [InlineData("""{ "type": "integer" }""", "0", true)]
    [InlineData("""{ "type": "integer" }""", "12345678910111213141516171819", true)]
    [InlineData("""{ "type": "integer" }""", "1.0", false)]
    [InlineData("""{ "type": "integer" }""", "1e2", false)]
    [InlineData("""{ "type": "integer" }""", "1E2", false)]
    [InlineData("""{ "type": "integer" }""", "1.5", false)]
    [InlineData("""{ "type": ["integer", "string"] }""", "1.0", false)]
    [InlineData("""{ "type": ["integer", "string"] }""", "\"a\"", true)]
    [InlineData("""{ "type": ["integer", "number"] }""", "1.0", true)]
    [InlineData("""{ "type": "number" }""", "1.0", true)]
    [InlineData("""{ "not": { "type": "integer" } }""", "1.0", true)]
    [InlineData("""{ "not": { "type": "integer" } }""", "1", false)]
    [InlineData("""{ "enum": [1] }""", "1.0", true)]
    public void An_integer_is_written_as_one(string schema, string value, bool valid) =>
        Assert.Equal(valid, Import(schema).SafeParse(value).IsSuccess);

    [Fact]
    public void A_float_is_reported_as_the_wrong_type()
    {
        ParseIssue issue = Assert.Single(Import("""{ "type": "integer" }""").SafeParse("1.0").Issues);

        Assert.Equal(IssueCodes.InvalidType, issue.Code);
    }

    [Fact]
    public void Other_dialects_keep_accepting_1_0() =>
        Assert.True(Z.FromJsonSchema("""{ "type": "integer" }""", new() { DefaultDialect = JsonSchemaImportDialect.Draft06 }).SafeParse("1.0").IsSuccess);

    [Fact]
    public void Emitting_never_rejects_what_parsing_accepts()
    {
        const string Schema = """{ "oneOf": [{ "type": "integer" }, { "minimum": 2 }], "not": { "type": "integer" } }""";
        EmitResult result = Import(Schema).Emit();

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Keyword == "oneOf");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Keyword == "not");
        Assert.True(Import(Schema).SafeParse("2.0").IsSuccess);
        Assert.True(Z.FromJsonSchema(result.Schema.ToJsonString()).SafeParse("2.0").IsSuccess);
    }

    [Fact]
    public void A_nested_float_is_reported_where_it_is()
    {
        JsonValueSchema schema = Import("""{ "properties": { "a": { "type": "integer" } } }""");

        Assert.Equal("/a", schema.SafeParse("""{ "a": 1.0 }""").Issues.Single().InstanceLocation);
    }
}
