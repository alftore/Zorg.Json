using System;
using Xunit;
using Zorg.Json.Emission;
using Zorg.Json.Import;

namespace Zorg.Json.Tests;

public sealed class OpenApi30ImportTests
{
    private static readonly JsonSchemaImportOptions OpenApi = new() { DefaultDialect = JsonSchemaImportDialect.OpenApi30 };

    private static JsonValueSchema Import(string schema) => Z.FromJsonSchema(schema, OpenApi);

    private static JsonSchemaImportException Refused(string schema, JsonSchemaImportOptions? options = null) =>
        Assert.Throws<JsonSchemaImportException>(() => Z.FromJsonSchema(schema, options ?? OpenApi));

    private static void AssertVerdicts(string schema, params (string Instance, bool Valid)[] cases)
    {
        JsonValueSchema imported = Import(schema);

        // The same document, emitted as Draft 2020-12 and read again, has to agree.
        JsonValueSchema again = Z.FromJsonSchema(imported.ToJsonSchema().ToJsonString());

        foreach ((string instance, bool valid) in cases)
        {
            Assert.True(valid == imported.SafeParse(instance).IsSuccess, $"{instance} in {schema}");
            Assert.True(valid == again.SafeParse(instance).IsSuccess, $"{instance} after emitting {schema}");
        }
    }

    [Fact]
    public void Nullable_allows_null_beside_a_type() =>
        AssertVerdicts("""{ "type": "string", "nullable": true }""", ("null", true), ("\"a\"", true), ("1", false));

    [Theory]
    [InlineData("""{ "type": "string" }""")]
    [InlineData("""{ "type": "string", "nullable": false }""")]
    public void Null_is_not_allowed_without_nullable(string schema) =>
        AssertVerdicts(schema, ("null", false), ("\"a\"", true));

    [Fact]
    public void Nullable_has_no_effect_without_a_type() =>
        AssertVerdicts("""{ "nullable": true, "enum": ["a"] }""", ("null", false), ("\"a\"", true));

    [Fact]
    public void Nullable_does_not_add_null_to_an_enum()
    {
        AssertVerdicts("""{ "type": "string", "nullable": true, "enum": ["a"] }""", ("null", false), ("\"a\"", true));
        AssertVerdicts("""{ "type": "string", "nullable": true, "enum": ["a", null] }""", ("null", true), ("\"a\"", true), ("\"b\"", false));
    }

    [Fact]
    public void Nullable_is_read_where_zod_writes_it() =>
        AssertVerdicts("""{ "type": "string", "nullable": true, "enum": [null] }""", ("null", true), ("\"a\"", false));

    [Fact]
    public void Nullable_applies_inside_properties_and_items() =>
        AssertVerdicts(
            """{ "type": "object", "properties": { "a": { "type": "array", "items": { "type": "integer", "nullable": true } } } }""",
            ("""{ "a": [1, null] }""", true),
            ("""{ "a": [1, "x"] }""", false),
            ("""{ "a": null }""", false));

    [Fact]
    public void Nullable_must_be_a_boolean() =>
        Assert.Equal(JsonSchemaImportCodes.InvalidSchema, Refused("""{ "type": "string", "nullable": "yes" }""").Code);

    [Fact]
    public void Nullable_beside_ref_is_ignored_like_the_other_siblings()
    {
        AssertVerdicts(
            """{ "$ref": "#/definitions/a", "nullable": true, "definitions": { "a": { "type": "string" } } }""",
            ("null", false),
            ("\"a\"", true));
    }

    [Theory]
    [InlineData("""{ "minimum": 1, "exclusiveMinimum": true }""", "1", false)]
    [InlineData("""{ "minimum": 1, "exclusiveMinimum": true }""", "1.5", true)]
    [InlineData("""{ "minimum": 1, "exclusiveMinimum": false }""", "1", true)]
    [InlineData("""{ "maximum": 1, "exclusiveMaximum": true }""", "1", false)]
    [InlineData("""{ "maximum": 1, "exclusiveMaximum": true }""", "0.5", true)]
    public void Exclusive_limits_are_booleans(string schema, string value, bool valid) =>
        AssertVerdicts(schema, (value, valid));

    [Theory]
    [InlineData("""{ "exclusiveMinimum": 1 }""", "/exclusiveMinimum")]
    [InlineData("""{ "exclusiveMaximum": true }""", "/exclusiveMaximum")]
    public void An_exclusive_limit_must_match_the_dialect(string schema, string location)
    {
        JsonSchemaImportException exception = Refused(schema);
        Assert.Equal(JsonSchemaImportCodes.InvalidSchema, exception.Code);
        Assert.Equal(location, exception.SchemaLocation);
    }

    [Fact]
    public void An_integer_has_no_fraction_or_exponent()
    {
        // As in Draft-04, so emitting it loosens it: no other dialect can tell 1.0 from 1.
        JsonValueSchema schema = Import("""{ "type": "integer" }""");

        Assert.True(schema.SafeParse("1").IsSuccess);
        Assert.False(schema.SafeParse("1.0").IsSuccess);
        Assert.False(schema.SafeParse("1e2").IsSuccess);
    }

    [Fact]
    public void Example_is_an_annotation()
    {
        JsonValueSchema schema = Import("""{ "type": "string", "example": "x" }""");

        Assert.Contains("\"examples\":[\"x\"]", schema.ToJsonSchema().ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("\"example\":\"x\"", schema.ToJsonSchema(new JsonSchemaOptions { Target = JsonSchemaProfile.OpenApi30 }).ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Nullable_comes_back_out_as_nullable()
    {
        JsonValueSchema schema = Import("""{ "type": "string", "nullable": true }""");
        string openApi = schema.ToJsonSchema(new JsonSchemaOptions { Target = JsonSchemaProfile.OpenApi30 }).ToJsonString();

        Assert.Equal("""{"type":"string","nullable":true}""", openApi);
        Assert.Equal("""{"$schema":"https://json-schema.org/draft/2020-12/schema","type":["string","null"]}""", schema.ToJsonSchema().ToJsonString());
    }

    [Fact]
    public void The_keywords_of_the_dialect_are_known()
    {
        JsonSchemaImportOptions strict = OpenApi with { UnknownKeywords = UnknownKeywordHandling.Error };

        _ = Z.FromJsonSchema(
            """{ "type": "string", "nullable": true, "readOnly": true, "writeOnly": false, "deprecated": true, "example": "a", "discriminator": { "propertyName": "p" }, "xml": { "name": "n" }, "externalDocs": { "url": "http://x.test" } }""",
            strict);

        Assert.Equal(JsonSchemaImportCodes.UnsupportedKeyword, Refused("""{ "type": "string", "frobnicate": 1 }""", strict).Code);
    }

    [Fact]
    public void Keywords_the_dialect_does_not_have_do_nothing_and_are_kept()
    {
        JsonValueSchema schema = Import("""{ "const": 1, "patternProperties": { "^a": { "type": "string" } }, "propertyNames": { "maxLength": 1 }, "contains": { "type": "string" } }""");

        Assert.True(schema.SafeParse("""{ "abc": 1 }""").IsSuccess);
        Assert.True(schema.SafeParse("2").IsSuccess);

        string emitted = schema.ToJsonSchema().ToJsonString();
        Assert.Contains("x-inert-const", emitted, StringComparison.Ordinal);
        Assert.Contains("x-inert-patternProperties", emitted, StringComparison.Ordinal);
        Assert.DoesNotContain("\"const\"", emitted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "type": ["string", "null"] }""", "/type")]
    [InlineData("""{ "type": "null" }""", "/type")]
    [InlineData("""{ "type": "array", "items": [{ "type": "string" }] }""", "/items")]
    [InlineData("""{ "items": true }""", "/items")]
    [InlineData("""{ "properties": { "a": false } }""", "/properties/a")]
    public void Forms_the_dialect_does_not_have_are_refused(string schema, string location)
    {
        JsonSchemaImportException exception = Refused(schema);
        Assert.Equal(JsonSchemaImportCodes.InvalidSchema, exception.Code);
        Assert.Equal(location, exception.SchemaLocation);
    }

    [Fact]
    public void Additional_properties_may_be_a_boolean() =>
        AssertVerdicts("""{ "properties": { "a": {} }, "additionalProperties": false }""", ("""{ "a": 1 }""", true), ("""{ "b": 1 }""", false));

    [Fact]
    public void Definitions_are_known_because_zod_writes_them() =>
        AssertVerdicts(
            """{ "type": "array", "items": { "$ref": "#/definitions/n" }, "definitions": { "n": { "type": "number", "nullable": true } } }""",
            ("[1, null]", true),
            ("""[1, "a"]""", false));

    [Fact]
    public void A_reference_into_components_is_followed_where_the_document_has_them()
    {
        AssertVerdicts(
            """
            {
              "$ref": "#/components/schemas/Pet",
              "components": { "schemas": {
                "Pet": { "type": "object", "required": ["name"], "properties": { "name": { "type": "string", "nullable": true }, "tag": { "$ref": "#/components/schemas/Tag" } } },
                "Tag": { "type": "string", "minLength": 2 }
              } }
            }
            """,
            ("""{ "name": null }""", true),
            ("""{ "name": "a", "tag": "ab" }""", true),
            ("""{ "name": "a", "tag": "a" }""", false),
            ("""{ }""", false));
    }

    [Fact]
    public void A_reference_into_components_the_document_lacks_is_an_unresolved_ref()
    {
        JsonSchemaImportException exception = Refused("""{ "$ref": "#/components/schemas/Pet" }""");

        Assert.Equal(JsonSchemaImportCodes.UnresolvedRef, exception.Code);
        Assert.Equal("/$ref", exception.SchemaLocation);
        Assert.Contains("components", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "type": "string", "nullable": true }""", "/nullable")]
    [InlineData("""{ "type": "string", "nullable": false }""", "/nullable")]
    [InlineData("""{ "properties": { "a": { "type": "array", "items": { "type": "string", "nullable": true } } } }""", "/properties/a/items/nullable")]
    public void Nullable_with_no_chosen_dialect_is_not_ignored(string schema, string location)
    {
        JsonSchemaImportException exception = Assert.Throws<JsonSchemaImportException>(() => Z.FromJsonSchema(schema));

        Assert.Equal(JsonSchemaImportCodes.UnsupportedDialect, exception.Code);
        Assert.Equal(location, exception.SchemaLocation);
        Assert.Contains("DefaultDialect = JsonSchemaImportDialect.OpenApi30", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "properties": { "nullable": { "type": "boolean" } } }""")]
    [InlineData("""{ "enum": [{ "nullable": true }] }""")]
    [InlineData("""{ "$schema": "https://json-schema.org/draft/2020-12/schema", "nullable": true }""")]
    [InlineData("""{ "$schema": "http://json-schema.org/draft-07/schema#", "type": "string", "nullable": true }""")]
    [InlineData("""{ "nullable": "maybe" }""")]
    public void Nullable_is_only_refused_as_a_keyword_of_a_document_with_no_dialect(string schema) =>
        _ = Z.FromJsonSchema(schema);

    [Fact]
    public void A_chosen_draft_keeps_nullable_as_an_unknown_keyword()
    {
        JsonValueSchema schema = Z.FromJsonSchema(
            """{ "type": "string", "nullable": true }""",
            new JsonSchemaImportOptions { DefaultDialect = JsonSchemaImportDialect.Draft07 });

        Assert.False(schema.SafeParse("null").IsSuccess);
    }

    [Fact]
    public void A_declared_dialect_wins_over_the_default()
    {
        JsonValueSchema schema = Import("""{ "$schema": "http://json-schema.org/draft-07/schema#", "const": 1 }""");

        Assert.True(schema.SafeParse("1").IsSuccess);
        Assert.False(schema.SafeParse("2").IsSuccess);
    }
}
