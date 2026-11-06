using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;
using Zorg.Json.Emission;
using Zorg.Json.Import;
using Zorg.Json.Model;

namespace Zorg.Json.Tests;

public sealed record WeatherArgs(string City, int Days);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WeatherArgs))]
public sealed partial class ImportJsonContext : JsonSerializerContext;

public sealed class JsonSchemaImportTests
{
    private const string Draft07 = "\"$schema\": \"http://json-schema.org/draft-07/schema#\"";
    private const string Draft06 = "\"$schema\": \"http://json-schema.org/draft-06/schema#\"";

    private static JsonSchemaImportException Failure(string schema, JsonSchemaImportOptions? options = null) =>
        Assert.Throws<JsonSchemaImportException>(() => Z.FromJsonSchema(schema, options));

    private static void AssertFails(string schema, string code, string location, JsonSchemaImportOptions? options = null)
    {
        JsonSchemaImportException exception = Failure(schema, options);
        Assert.Equal(code, exception.Code);
        Assert.Equal(location, exception.SchemaLocation);
    }

    private static string Emit(string schema, JsonSchemaOptions? options = null) =>
        Z.FromJsonSchema(schema).ToJsonSchema(options).ToJsonString();

    private static string EmitBody(string schema) =>
        Emit(schema).Replace("\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",", string.Empty, System.StringComparison.Ordinal);

    [Theory]
    [InlineData("""{ "$dynamicRef": "#a" }""", "/$dynamicRef")]
    [InlineData("""{ "$defs": { "a": { "$dynamicAnchor": "a" } } }""", "/$defs/a/$dynamicAnchor")]
    [InlineData("""{ "unevaluatedProperties": false, "allOf": [{}] }""", "/unevaluatedProperties")]
    [InlineData("""{ "unevaluatedProperties": false, "anyOf": [{}] }""", "/unevaluatedProperties")]
    [InlineData("""{ "unevaluatedProperties": false, "oneOf": [{}] }""", "/unevaluatedProperties")]
    [InlineData("""{ "unevaluatedProperties": false, "if": {} }""", "/unevaluatedProperties")]
    [InlineData("""{ "unevaluatedProperties": false, "$ref": "#/$defs/a", "$defs": { "a": {} } }""", "/unevaluatedProperties")]
    [InlineData("""{ "unevaluatedProperties": false, "dependentSchemas": { "a": {} } }""", "/unevaluatedProperties")]
    [InlineData("""{ "unevaluatedItems": false, "contains": {} }""", "/unevaluatedItems")]
    public void Keywords_it_cant_honour_are_errors(string schema, string location) =>
        AssertFails(schema, JsonSchemaImportCodes.UnsupportedKeyword, location);

    [Fact]
    public void Unevaluated_properties_alone_acts_like_additional_properties()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""{ "properties": { "a": {} }, "unevaluatedProperties": false }""");

        Assert.True(schema.SafeParse("""{ "a": 1 }""").IsSuccess);
        Assert.False(schema.SafeParse("""{ "b": 1 }""").IsSuccess);
        Assert.Contains("unevaluatedProperties", Emit("""{ "properties": { "a": {} }, "unevaluatedProperties": false }"""), System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://json-schema.org/draft-03/schema#")]
    [InlineData("https://json-schema.org/draft/2019-09/schema")]
    [InlineData("https://spec.openapis.org/oas/3.0/schema/2021-09-28")]
    [InlineData("https://example.com/my-meta-schema")]
    public void Other_dialects_are_refused(string dialect)
    {
        string schema = $$"""{ "$schema": "{{dialect}}" }""";

        AssertFails(schema, JsonSchemaImportCodes.UnsupportedDialect, "/$schema");
        Assert.Contains("Draft 2020-12, draft-07, draft-06 or draft-04", Failure(schema).Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_subschema_cant_change_the_dialect() =>
        AssertFails($$"""{ "$defs": { "a": { {{Draft07}} } } }""", JsonSchemaImportCodes.UnsupportedDialect, "/$defs/a/$schema");

    [Theory]
    [InlineData("""{ "pattern": "(" }""", "/pattern")]
    [InlineData("""{ "patternProperties": { "[": {} } }""", "/patternProperties/[")]
    public void Malformed_patterns_are_errors(string schema, string location) =>
        AssertFails(schema, JsonSchemaImportCodes.InvalidPattern, location);

    [Theory]
    [InlineData("1", "")]
    [InlineData("""{ "properties": { "a": 1 } }""", "/properties/a")]
    [InlineData("""{ "type": "text" }""", "/type")]
    [InlineData("""{ "type": ["string", "string"] }""", "/type")]
    [InlineData("""{ "type": [] }""", "/type")]
    [InlineData("""{ "minLength": -1 }""", "/minLength")]
    [InlineData("""{ "minLength": 1.5 }""", "/minLength")]
    [InlineData("""{ "minimum": "1" }""", "/minimum")]
    [InlineData("""{ "exclusiveMinimum": true }""", "/exclusiveMinimum")]
    [InlineData("""{ "multipleOf": 0 }""", "/multipleOf")]
    [InlineData("""{ "required": "a" }""", "/required")]
    [InlineData("""{ "required": ["a", "a"] }""", "/required")]
    [InlineData("""{ "uniqueItems": 1 }""", "/uniqueItems")]
    [InlineData("""{ "allOf": [] }""", "/allOf")]
    [InlineData("""{ "items": [{}] }""", "/items")]
    [InlineData("""{ "$id": "#a" }""", "/$id")]
    [InlineData("""{ "$anchor": "1a" }""", "/$anchor")]
    [InlineData("""{ "$defs": { "a": { "$anchor": "b" }, "c": { "$anchor": "b" } } }""", "/$defs/c")]
    [InlineData("""{ "dependentRequired": { "a": "b" } }""", "/dependentRequired/a")]
    [InlineData("""{ "$schema": 1 }""", "/$schema")]
    public void Keyword_values_of_the_wrong_shape_are_errors(string schema, string location) =>
        AssertFails(schema, JsonSchemaImportCodes.InvalidSchema, location);

    [Theory]
    [InlineData("""{ "type": "string" """)]
    [InlineData("""{ "type": "string", "type": "number" }""")]
    public void Unusable_text_is_an_error(string schema) =>
        AssertFails(schema, JsonSchemaImportCodes.InvalidSchema, string.Empty);

    [Fact]
    public void An_element_with_a_repeated_keyword_is_an_error()
    {
        JsonElement element = JsonDocument.Parse("""{ "type": "string", "type": "number" }""").RootElement;
        JsonSchemaImportException exception = Assert.Throws<JsonSchemaImportException>(() => Z.FromJsonSchema(element));
        Assert.Equal(JsonSchemaImportCodes.InvalidSchema, exception.Code);
    }

    [Theory]
    [InlineData("""{ "$ref": "#" }""", "/$ref")]
    [InlineData("""{ "allOf": [{ "$ref": "#" }] }""", "/allOf/0/$ref")]
    [InlineData("""{ "anyOf": [{ "type": "string" }, { "$ref": "#" }] }""", "/anyOf/1/$ref")]
    [InlineData("""{ "not": { "$ref": "#" } }""", "/not/$ref")]
    [InlineData("""{ "if": { "$ref": "#" } }""", "/if/$ref")]
    [InlineData("""{ "dependentSchemas": { "a": { "$ref": "#" } } }""", "/dependentSchemas/a/$ref")]
    [InlineData("""{ "$defs": { "a": { "$ref": "#/$defs/b" }, "b": { "$ref": "#/$defs/a" } } }""", "/$defs/b/$ref")]
    public void Cycles_that_consume_no_input_are_errors(string schema, string location) =>
        AssertFails(schema, JsonSchemaImportCodes.RefCycle, location);

    [Theory]
    [InlineData("""{ "properties": { "next": { "$ref": "#" } } }""")]
    [InlineData("""{ "items": { "$ref": "#" } }""")]
    [InlineData("""{ "additionalProperties": { "$ref": "#" } }""")]
    [InlineData("""{ "contains": { "$ref": "#" } }""")]
    [InlineData("""{ "propertyNames": { "$ref": "#" } }""")]
    public void Recursion_through_children_is_fine(string schema)
    {
        Assert.True(Z.FromJsonSchema(schema).SafeParse("{}").IsSuccess);
    }

    [Fact]
    public void Recursive_schemas_validate_and_stay_shared()
    {
        const string Text = """
            {
              "$defs": { "tree": { "type": "object", "properties": { "children": { "type": "array", "items": { "$ref": "#/$defs/tree" } } }, "required": ["children"] } },
              "$ref": "#/$defs/tree"
            }
            """;

        JsonValueSchema schema = Z.FromJsonSchema(Text);

        Assert.True(schema.SafeParse("""{ "children": [{ "children": [] }] }""").IsSuccess);
        Assert.False(schema.SafeParse("""{ "children": [{}] }""").IsSuccess);
        Assert.Equal("#/$defs/tree", schema.ToNode().Ref);
        Assert.Null(schema.ToNode().Behavior.DefinitionName);
        Assert.Same(schema.ToNode().Defs[0].Schema, schema.ToNode().Behavior.Target!());
    }

    [Theory]
    [InlineData("""{ "$ref": "https://example.com/schema.json" }""", "/$ref")]
    [InlineData("""{ "$ref": "other.json#/a" }""", "/$ref")]
    [InlineData("""{ "$ref": "#/$defs/missing" }""", "/$ref")]
    [InlineData("""{ "$ref": "#missing" }""", "/$ref")]
    [InlineData("""{ "$ref": "#/title", "title": "a" }""", "/$ref")]
    [InlineData("""{ "properties": { "a": { "$ref": "#/$defs/missing" } } }""", "/properties/a/$ref")]
    public void Refs_that_dont_resolve_locally_are_errors(string schema, string location) =>
        AssertFails(schema, JsonSchemaImportCodes.UnresolvedRef, location);

    [Fact]
    public void Refs_resolve_pointers_anchors_and_escapes()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""
            {
              "$defs": { "a/b": { "type": "string" }, "c~d": { "type": "integer" }, "e f": { "$anchor": "named", "type": "boolean" } },
              "properties": {
                "slash": { "$ref": "#/$defs/a~1b" },
                "tilde": { "$ref": "#/$defs/c~0d" },
                "space": { "$ref": "#/$defs/e%20f" },
                "anchor": { "$ref": "#named" },
                "list": { "allOf": [{ "type": "null" }] },
                "index": { "$ref": "#/properties/list/allOf/0" }
              }
            }
            """);

        Assert.True(schema.SafeParse("""{ "slash": "a", "tilde": 1, "space": true, "anchor": false, "index": null }""").IsSuccess);
        Assert.Equal(5, schema.SafeParse("""{ "slash": 1, "tilde": "a", "space": 1, "anchor": 1, "index": 1 }""").Issues.Length);
    }

    [Fact]
    public void A_ref_can_point_into_an_unknown_keyword()
    {
        const string Text = """{ "x-shared": { "type": "string" }, "properties": { "a": { "$ref": "#/x-shared" } } }""";

        Assert.False(Z.FromJsonSchema(Text).SafeParse("""{ "a": 1 }""").IsSuccess);
        Assert.False(Z.FromJsonSchema(Emit(Text)).SafeParse("""{ "a": 1 }""").IsSuccess);
    }

    [Fact]
    public void A_fragment_resolves_inside_its_own_id_resource()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""
            {
              "$defs": {
                "inner": { "$id": "https://example.com/inner", "$defs": { "a": { "type": "string" } }, "properties": { "x": { "$ref": "#/$defs/a" } } },
                "a": { "type": "integer" }
              },
              "$ref": "#/$defs/inner"
            }
            """);

        Assert.True(schema.SafeParse("""{ "x": "text" }""").IsSuccess);
        Assert.False(schema.SafeParse("""{ "x": 1 }""").IsSuccess);
    }

    [Fact]
    public void Siblings_of_ref_apply_in_2020_12()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""{ "$defs": { "a": { "type": "string" } }, "$ref": "#/$defs/a", "maxLength": 1 }""");

        Assert.False(schema.SafeParse("\"ab\"").IsSuccess);
        Assert.False(schema.SafeParse("1").IsSuccess);
    }

    [Fact]
    public void Siblings_of_ref_are_ignored_in_draft_07_and_kept_inert()
    {
        string text = $$"""{ {{Draft07}}, "definitions": { "a": { "type": "string" } }, "$ref": "#/definitions/a", "maxLength": 1, "title": "kept" }""";

        Assert.True(Z.FromJsonSchema(text).SafeParse("\"ab\"").IsSuccess);
        Assert.Equal(
            """{"$ref":"#/$defs/a","title":"kept","$defs":{"a":{"type":"string"}},"x-inert-maxLength":1}""",
            EmitBody(text));
        Assert.True(Z.FromJsonSchema(Emit(text)).SafeParse("\"ab\"").IsSuccess);
    }

    [Fact]
    public void Draft_07_keywords_are_normalized()
    {
        string text = $$"""
            {
              {{Draft07}},
              "definitions": { "name": { "type": "string" } },
              "items": [{ "$ref": "#/definitions/name" }, { "$ref": "#/items/0" }],
              "additionalItems": false,
              "dependencies": { "a": ["b"], "c": { "required": ["d"] } }
            }
            """;

        SchemaNode node = Z.FromJsonSchema(text).ToNode();

        Assert.Equal("name", node.Defs[0].Name);
        Assert.Equal(2, node.PrefixItems.Length);
        Assert.Equal("#/$defs/name", node.PrefixItems[0].Ref);
        Assert.Equal("#/prefixItems/0", node.PrefixItems[1].Ref);
        Assert.Equal(false, node.Items!.BooleanValue);
        Assert.Equal("a", node.DependentRequired[0].Key);
        Assert.Equal("c", node.DependentSchemas[0].Name);

        JsonValueSchema schema = Z.FromJsonSchema(text);
        Assert.True(schema.SafeParse("""["a", "b"]""").IsSuccess);
        Assert.False(schema.SafeParse("""["a", 1]""").IsSuccess);
        Assert.False(schema.SafeParse("""["a", "b", "c"]""").IsSuccess);
        Assert.False(schema.SafeParse("""{ "a": 1 }""").IsSuccess);
        Assert.False(schema.SafeParse("""{ "c": 1 }""").IsSuccess);
    }

    [Fact]
    public void Draft_07_anchors_become_anchors()
    {
        string text = $$"""{ {{Draft07}}, "definitions": { "a": { "$id": "#named", "type": "string" } }, "$ref": "#named" }""";

        Assert.False(Z.FromJsonSchema(text).SafeParse("1").IsSuccess);
        Assert.Contains("\"$anchor\":\"named\"", Emit(text), System.StringComparison.Ordinal);
        Assert.Contains("\"$id\":\"#named\"", Emit(text, new() { Target = JsonSchemaProfile.Draft07 }), System.StringComparison.Ordinal);
    }

    [Fact]
    public void Keywords_from_later_drafts_stay_inert()
    {
        string text = $$"""{ {{Draft07}}, "dependentRequired": { "a": ["b"] }, "minContains": 2, "contains": { "type": "string" } }""";

        Assert.True(Z.FromJsonSchema(text).SafeParse("""{ "a": 1 }""").IsSuccess);
        Assert.True(Z.FromJsonSchema(Emit(text)).SafeParse("""{ "a": 1 }""").IsSuccess);
        Assert.True(Z.FromJsonSchema(Emit(text)).SafeParse("""["a"]""").IsSuccess);
    }

    [Fact]
    public void Draft_06_has_no_conditionals()
    {
        string text = $$"""{ {{Draft06}}, "if": { "type": "string" }, "then": false }""";

        Assert.True(Z.FromJsonSchema(text).SafeParse("\"a\"").IsSuccess);
        Assert.True(Z.FromJsonSchema(Emit(text)).SafeParse("\"a\"").IsSuccess);
    }

    [Fact]
    public void Draft_keywords_in_2020_12_are_unknown()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""{ "dependencies": { "a": ["b"] }, "additionalItems": false }""");

        Assert.True(schema.SafeParse("""{ "a": 1 }""").IsSuccess);
        Assert.Equal(2, schema.ToNode().Extensions.Length);
        Assert.DoesNotContain("dependencies", schema.ToJsonSchema(new() { Target = JsonSchemaProfile.Draft07 }).ToJsonString(), System.StringComparison.Ordinal);
    }

    [Fact]
    public void The_default_dialect_can_be_changed()
    {
        JsonSchemaImportOptions options = new() { DefaultDialect = JsonSchemaImportDialect.Draft07 };
        const string Text = """{ "items": [{ "type": "string" }] }""";

        Assert.True(Z.FromJsonSchema(Text, options).SafeParse("""["a"]""").IsSuccess);
        AssertFails(Text, JsonSchemaImportCodes.InvalidSchema, "/items");
    }

    [Fact]
    public void Absent_additional_properties_means_open()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""{ "type": "object", "properties": { "a": { "type": "string" } } }""");

        Assert.True(schema.SafeParse("""{ "a": "x", "b": 1 }""").IsSuccess);
        Assert.Equal(UnknownProperties.Allow, schema.ToNode().Behavior.UnknownProperties);
        Assert.DoesNotContain("additionalProperties", schema.ToJsonSchema().ToJsonString(), System.StringComparison.Ordinal);
    }

    [Fact]
    public void Default_is_only_an_annotation()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""{ "properties": { "a": { "default": 1 } } }""");

        Assert.Equal("{}", schema.Parse("{}").GetRawText());
        Assert.False(schema.ToNode().Properties[0].Schema.Behavior.FillDefault);
    }

    [Fact]
    public void Content_keywords_are_only_annotations()
    {
        const string Text = """{ "contentEncoding": "base64", "contentMediaType": "application/json", "contentSchema": { "type": "object" } }""";

        Assert.True(Z.FromJsonSchema(Text).SafeParse("\"!!not base64\"").IsSuccess);
        Assert.Equal(
            """{"contentEncoding":"base64","contentMediaType":"application/json","contentSchema":{"type":"object"}}""",
            EmitBody(Text));
    }

    [Fact]
    public void An_empty_enum_rejects_everything()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""{ "enum": [] }""");

        Assert.False(schema.SafeParse("1").IsSuccess);
        Assert.False(Z.FromJsonSchema(schema.ToJsonSchema().ToJsonString()).SafeParse("1").IsSuccess);
    }

    [Theory]
    [InlineData("""{ "const": 1 }""", "1.0")]
    [InlineData("""{ "const": 1.0 }""", "1")]
    [InlineData("""{ "const": 100 }""", "1e2")]
    [InlineData("""{ "const": 0 }""", "-0")]
    [InlineData("""{ "enum": [12345678901234567890123] }""", "12345678901234567890123.0")]
    [InlineData("""{ "const": { "a": [1, { "b": 2 }] } }""", """{ "a": [1.0, { "b": 2e0 }] }""")]
    [InlineData("""{ "uniqueItems": false }""", "[1, 1.0]")]
    public void Numbers_compare_by_value(string schema, string document) =>
        Assert.True(Z.FromJsonSchema(schema).SafeParse(document).IsSuccess);

    [Theory]
    [InlineData("""{ "const": 12345678901234567890123 }""", "12345678901234567890124")]
    [InlineData("""{ "uniqueItems": true }""", "[1, 1.0]")]
    [InlineData("""{ "uniqueItems": true }""", "[{ \"a\": 1, \"b\": 2 }, { \"b\": 2.0, \"a\": 1 }]")]
    public void Different_values_and_duplicates_are_detected(string schema, string document) =>
        Assert.False(Z.FromJsonSchema(schema).SafeParse(document).IsSuccess);

    [Fact]
    public void Unknown_keywords_survive()
    {
        const string Text = """{ "x-order": 1, "vendorThing": { "a": [true] }, "$vocabulary": {} }""";

        Assert.Equal("""{"$vocabulary":{},"vendorThing":{"a":[true]},"x-order":1}""", EmitBody(Text));
    }

    [Fact]
    public void Unknown_keywords_can_be_errors()
    {
        JsonSchemaImportOptions options = new() { UnknownKeywords = UnknownKeywordHandling.Error };

        AssertFails("""{ "vendorThing": 1 }""", JsonSchemaImportCodes.UnsupportedKeyword, "/vendorThing", options);
        Assert.NotNull(Z.FromJsonSchema("""{ "x-order": 1 }""", options));
    }

    [Fact]
    public void Formats_are_annotations_by_default_in_2020_12_as_the_specification_requires()
    {
        Assert.True(Z.FromJsonSchema("""{ "format": "ipv4" }""").SafeParse("\"1.2.3\"").IsSuccess);
        Assert.True(Z.FromJsonSchema("""{ "$schema": "https://json-schema.org/draft/2020-12/schema", "format": "ipv4" }""").SafeParse("\"1.2.3\"").IsSuccess);
    }

    [Theory]
    [InlineData("http://json-schema.org/draft-07/schema#")]
    [InlineData("http://json-schema.org/draft-06/schema#")]
    [InlineData("http://json-schema.org/draft-04/schema#")]
    public void Older_drafts_allow_checking_formats_and_keep_it(string dialect)
    {
        JsonValueSchema schema = Z.FromJsonSchema($$"""{ "$schema": "{{dialect}}", "format": "ipv4" }""");

        Assert.False(schema.SafeParse("\"1.2.3\"").IsSuccess);
        Assert.True(schema.SafeParse("1").IsSuccess);
    }

    [Fact]
    public void Formats_can_be_asserted_in_2020_12()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""{ "format": "ipv4" }""", new() { Formats = FormatHandling.AssertKnown });

        Assert.False(schema.SafeParse("\"1.2.3\"").IsSuccess);
        Assert.True(schema.SafeParse("1").IsSuccess);
    }

    [Fact]
    public void Formats_can_be_annotations()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""{ "format": "ipv4" }""", new() { Formats = FormatHandling.AnnotateOnly });

        Assert.True(schema.SafeParse("\"1.2.3\"").IsSuccess);
        Assert.Contains("ipv4", schema.ToJsonSchema().ToJsonString(), System.StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_formats_are_annotations_unless_told_otherwise()
    {
        Assert.True(Z.FromJsonSchema("""{ "format": "made-up" }""").SafeParse("\"a\"").IsSuccess);
        AssertFails("""{ "format": "made-up" }""", JsonSchemaImportCodes.UnsupportedKeyword, "/format", new() { UnknownFormats = UnknownFormatHandling.Error });
    }

    [Fact]
    public void The_document_can_be_disposed_after_import()
    {
        JsonValueSchema schema;

        using (JsonDocument document = JsonDocument.Parse("""{ "const": { "a": 1 }, "default": "x" }"""))
        {
            schema = Z.FromJsonSchema(document.RootElement);
        }

        Assert.True(schema.SafeParse("""{ "a": 1 }""").IsSuccess);
        Assert.Contains("\"default\":\"x\"", schema.ToJsonSchema().ToJsonString(), System.StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_validates_then_deserializes()
    {
        Schema<WeatherArgs> schema = Z.FromJsonSchema("""
            { "type": "object", "properties": { "city": { "type": "string", "minLength": 1 }, "days": { "type": "integer" } }, "required": ["city"] }
            """).Bind(ImportJsonContext.Default.WeatherArgs);

        Assert.Equal(new WeatherArgs("Oslo", 3), schema.Parse("""{ "city": "Oslo", "days": 3 }"""));
        Assert.Equal(IssueCodes.TooSmall, schema.SafeParse("""{ "city": "", "days": 3 }""").Issues[0].Code);
        Assert.Contains("minLength", schema.ToJsonSchema().ToJsonString(), System.StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_reports_what_System_Text_Json_rejects()
    {
        Schema<WeatherArgs> schema = Z.FromJsonSchema("""{ "type": "object" }""").Bind(ImportJsonContext.Default.WeatherArgs);

        Assert.Equal(IssueCodes.DeserializationFailed, schema.SafeParse("""{ "city": 1 }""").Issues[0].Code);
    }

    [Fact]
    public void Subschemas_may_nest_up_to_the_limit()
    {
        JsonSchemaImportOptions options = new() { MaxDepth = 3 };

        Assert.NotNull(Z.FromJsonSchema("""{ "not": { "not": { "not": {} } } }""", options));
        AssertFails("""{ "not": { "not": { "not": { "not": {} } } } }""", JsonSchemaImportCodes.LimitExceeded, "/not/not/not/not", options);
    }

    [Fact]
    public void Very_deep_text_exceeds_the_limit_instead_of_overflowing()
    {
        string text = string.Concat(Enumerable.Repeat("""{"not":""", 200_000)) + "{}" + new string('}', 200_000);

        Assert.Equal(JsonSchemaImportCodes.LimitExceeded, Failure(text).Code);
    }

    [Fact]
    public void Node_count_is_limited()
    {
        const string Text = """{ "allOf": [{}, {}, {}] }""";

        Assert.NotNull(Z.FromJsonSchema(Text, new() { MaxNodes = 4 }));
        AssertFails(Text, JsonSchemaImportCodes.LimitExceeded, "/allOf/2", new() { MaxNodes = 3 });
    }

    [Fact]
    public void A_ref_target_counts_once()
    {
        const string Text = """{ "$defs": { "a": {} }, "allOf": [{ "$ref": "#/$defs/a" }, { "$ref": "#/$defs/a" }] }""";

        Assert.NotNull(Z.FromJsonSchema(Text, new() { MaxNodes = 5 }));
    }

    [Fact]
    public void Patterns_are_limited()
    {
        JsonSchemaImportOptions options = new() { MaxPatternLength = 3 };

        Assert.NotNull(Z.FromJsonSchema("""{ "pattern": "abc" }""", options));
        AssertFails("""{ "pattern": "abcd" }""", JsonSchemaImportCodes.LimitExceeded, "/pattern", options);
        AssertFails("""{ "patternProperties": { "abcd": {} } }""", JsonSchemaImportCodes.LimitExceeded, "/patternProperties/abcd", options);
    }

    [Fact]
    public void Counts_beyond_int_are_limited() =>
        AssertFails("""{ "maxLength": 1e30 }""", JsonSchemaImportCodes.LimitExceeded, "/maxLength");

    [Fact]
    public void Shared_references_cant_multiply_into_exponential_work()
    {
        StringBuilder defs = new("""{ "$ref": "#/$defs/d0", "$defs": { """);

        for (int level = 0; level < 40; level++)
        {
            defs.Append(System.Globalization.CultureInfo.InvariantCulture, $$"""
                "d{{level}}": { "allOf": [{ "$ref": "#/$defs/d{{level + 1}}" }, { "$ref": "#/$defs/d{{level + 1}}" }] },
                """);
        }

        defs.Append(""" "d40": {} } }""");

        Assert.Equal(JsonSchemaImportCodes.LimitExceeded, Failure(defs.ToString(), new() { MaxDepth = 200 }).Code);
    }

    [Fact]
    public void A_long_chain_of_in_place_refs_is_limited()
    {
        StringBuilder defs = new("""{ "$ref": "#/$defs/d0", "$defs": { """);

        for (int level = 0; level < 100; level++)
        {
            defs.Append(System.Globalization.CultureInfo.InvariantCulture, $$""" "d{{level}}": { "$ref": "#/$defs/d{{level + 1}}" }, """);
        }

        defs.Append(""" "d100": {} } }""");

        Assert.Equal(JsonSchemaImportCodes.LimitExceeded, Failure(defs.ToString()).Code);
    }

    [Fact]
    public void A_long_chain_through_children_doesnt_exhaust_the_stack()
    {
        StringBuilder defs = new("""{ "$ref": "#/$defs/d0", "$defs": { """);

        for (int level = 0; level < 5000; level++)
        {
            defs.Append(System.Globalization.CultureInfo.InvariantCulture, $$""" "d{{level}}": { "properties": { "a": { "$ref": "#/$defs/d{{level + 1}}" } } }, """);
        }

        defs.Append(""" "d5000": {} } }""");

        JsonValueSchema schema = Z.FromJsonSchema(defs.ToString(), new() { MaxNodes = 20_000 });
        Assert.True(schema.SafeParse("""{ "a": { "a": {} } }""").IsSuccess);
    }

    [Fact]
    public void A_pattern_that_takes_too_long_in_a_property_name_is_an_issue()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""{ "patternProperties": { "^(a+)+\\1$": {} } }""");
        string name = new string('a', 40) + "!";

        Assert.False(schema.SafeParse($$"""{ "{{name}}": 1 }""").IsSuccess);
    }
}
