using System.Text.Json;
using Json.Schema;
using Xunit;
using Zorg.Json.Import;

namespace Zorg.Json.ComplianceTests;

/// <summary>
/// An imported legacy document, emitted as Draft 2020-12, has to mean what it meant before, and has to import again.
/// JsonSchema.Net reads the emitted text, so these don't depend on Zorg's own reading of it.
/// </summary>
public sealed class ImportEmitTests
{
    private const string Draft07 = "\"$schema\": \"http://json-schema.org/draft-07/schema#\"";
    private const string Draft04 = "\"$schema\": \"http://json-schema.org/draft-04/schema#\"";

    private static string AssertFaithful(string schema, params (string Instance, bool Valid)[] cases)
    {
        JsonValueSchema imported = Z.FromJsonSchema(schema);
        string text = imported.ToJsonSchema().ToJsonString();
        JsonSchema external = JsonSchema.FromText(text);
        JsonValueSchema again = Z.FromJsonSchema(text);

        Assert.True(MetaSchemas.Draft202012.Evaluate(JsonElement.Parse(text)).IsValid, $"not a 2020-12 schema: {text}");

        foreach ((string instance, bool valid) in cases)
        {
            Assert.True(valid == imported.SafeParse(instance).IsSuccess, $"imported, {instance}");
            Assert.True(valid == external.Evaluate(JsonElement.Parse(instance)).IsValid, $"emitted, {instance}: {text}");
            Assert.True(valid == again.SafeParse(instance).IsSuccess, $"imported again, {instance}: {text}");
        }

        Assert.Equal(text, again.ToJsonSchema().ToJsonString());
        return text;
    }

    [Fact]
    public void A_target_under_defs_is_read_with_the_rules_of_its_draft() =>
        AssertFaithful(
            $$"""{ {{Draft07}}, "$ref": "#/$defs/a", "$defs": { "a": { "$ref": "#/$defs/b", "minimum": 5 }, "b": { "type": "integer" } } }""",
            ("1", true),
            ("1.5", false),
            ("\"x\"", false));

    [Fact]
    public void A_target_under_an_unknown_keyword_is_read_with_the_rules_of_its_draft() =>
        AssertFaithful(
            $$"""{ {{Draft07}}, "$ref": "#/foo/a", "foo": { "a": { "$ref": "#/foo/b", "minimum": 5 }, "b": { "type": "integer" } } }""",
            ("1", true),
            ("1.5", false),
            ("\"x\"", false));

    [Fact]
    public void A_name_in_defs_does_not_take_over_a_name_in_definitions()
    {
        string text = AssertFaithful(
            $$"""{ {{Draft07}}, "properties": { "a": { "$ref": "#/$defs/x" } }, "definitions": { "x": { "type": "string" } }, "$defs": { "x": { "type": "integer" } } }""",
            ("""{ "a": 1 }""", true),
            ("""{ "a": "s" }""", false));

        Assert.Contains("x-inert-$defs", text, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_target_under_an_ignored_sibling_of_ref_is_still_there()
    {
        string text = AssertFaithful(
            $$"""{ {{Draft07}}, "$ref": "#/properties/a", "properties": { "a": { "type": "integer" } } }""",
            ("1", true),
            ("\"s\"", false));

        // The ignored properties stay under their inert name.
        Assert.Contains("x-inert-properties", text, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_target_inside_a_target_is_found()
    {
        AssertFaithful(
            $$"""{ {{Draft07}}, "items": { "$ref": "#/foo/a/properties/b" }, "foo": { "a": { "properties": { "b": { "exclusiveMinimum": 3 } } } } }""",
            ("[4]", true),
            ("[3]", false));
    }

    [Fact]
    public void An_id_with_a_fragment_becomes_an_id_and_an_anchor()
    {
        string text = AssertFaithful(
            $$"""{ {{Draft07}}, "$id": "http://example.test/y#frag", "type": "integer" }""",
            ("1", true),
            ("\"s\"", false));

        Assert.DoesNotContain("#frag", text, System.StringComparison.Ordinal);
        Assert.Contains("\"$anchor\":\"frag\"", text, System.StringComparison.Ordinal);
    }

    [Fact]
    public void An_id_with_a_fragment_that_is_not_a_plain_name_is_refused()
    {
        JsonSchemaImportException exception = Assert.Throws<JsonSchemaImportException>(
            () => Z.FromJsonSchema($$"""{ {{Draft07}}, "$id": "http://example.test/y#a b" }"""));

        Assert.Equal(JsonSchemaImportCodes.InvalidSchema, exception.Code);
        Assert.Equal("/$id", exception.SchemaLocation);
    }

    [Fact]
    public void A_draft_04_boolean_exclusive_limit_in_a_target_is_a_number_when_emitted() =>
        AssertFaithful(
            $$"""{ {{Draft04}}, "$ref": "#/foo", "foo": { "type": "number", "minimum": 1, "exclusiveMinimum": true } }""",
            ("1", false),
            ("2", true));

    [Fact]
    public void Draft_07_array_form_items_in_a_target_are_a_tuple() =>
        AssertFaithful(
            $$"""{ {{Draft07}}, "$ref": "#/$defs/t", "$defs": { "t": { "type": "array", "items": [{ "type": "integer" }, { "type": "string" }], "additionalItems": false } } }""",
            ("""[1, "a"]""", true),
            ("[1, 2]", false),
            ("""[1, "a", 3]""", false));

    [Fact]
    public void An_unreferenced_defs_is_not_read_as_a_schema()
    {
        string text = AssertFaithful(
            $$"""{ {{Draft07}}, "type": "integer", "$defs": { "a": { "type": "bogus" } } }""",
            ("1", true),
            ("\"s\"", false));

        Assert.Contains("x-inert-$defs", text, System.StringComparison.Ordinal);
        Assert.DoesNotContain("\"$defs\"", text, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_target_in_a_nested_resource_goes_with_that_resource() =>
        AssertFaithful(
            $$"""
            { {{Draft07}}, "properties": { "a": { "$id": "http://example.test/a", "properties": { "b": { "$ref": "#/$defs/t" } }, "$defs": { "t": { "type": "integer", "minimum": 5 } } } } }
            """,
            ("""{ "a": { "b": 5 } }""", true),
            ("""{ "a": { "b": 4 } }""", false),
            ("""{ "a": { "b": "s" } }""", false));

    [Fact]
    public void Emitting_for_draft_07_keeps_the_targets()
    {
        JsonValueSchema imported = Z.FromJsonSchema($$"""{ {{Draft07}}, "$ref": "#/foo/a", "foo": { "a": { "type": "integer", "exclusiveMinimum": 3 } } }""");
        string text = imported.ToJsonSchema(new Emission.JsonSchemaOptions { Target = Emission.JsonSchemaProfile.Draft07 }).ToJsonString();
        JsonSchema external = JsonSchema.FromText(text);

        Assert.True(external.Evaluate(JsonElement.Parse("4")).IsValid, text);
        Assert.False(external.Evaluate(JsonElement.Parse("3")).IsValid, text);
    }
}
