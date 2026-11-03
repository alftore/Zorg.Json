using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Zorg.Json.Emission;
using Zorg.Json.Model;

namespace Zorg.Json.Llm.Tests;

public sealed class RewritersTests
{
    private static JsonSchemaOptions With(params ISchemaRewriter[] rewriters) =>
        new() { Target = new JsonSchemaProfile("test", JsonSchemaDialect.OpenApi31, [.. rewriters], []) };

    private static string Json<T>(Schema<T> schema, params ISchemaRewriter[] rewriters) =>
        schema.ToJsonSchema(With(rewriters)).ToJsonString();

    [Fact]
    public void CloseAllObjects_closes_nested_open_objects()
    {
        ObjectSchema schema = Z.Object().Passthrough().Property("a", Z.Object().Strip());

        Assert.Equal(
            """{"type":"object","properties":{"a":{"type":"object","additionalProperties":false}},"required":["a"],"additionalProperties":false}""",
            Json(schema, Rewriters.CloseAllObjects));
    }

    [Fact]
    public void CloseAllObjects_moves_a_value_schema_to_the_description()
    {
        EmitResult result = Z.Record(Z.String()).Emit(With(Rewriters.CloseAllObjects));

        Assert.Equal("""Must also satisfy: additionalProperties {"type":"string"}.""", result.Schema["description"]!.GetValue<string>());
        Assert.False(result.Schema["additionalProperties"]!.GetValue<bool>());
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Keyword == "additionalProperties");
    }

    [Fact]
    public void RequireAllAsNullable_adds_null_to_optional_properties()
    {
        ObjectSchema schema = Z.Object()
            .Property("required", Z.String())
            .Property("optional", Z.String().Optional())
            .Property("choice", Z.String().Enum("a", "b").Optional())
            .Property("nested", Z.Object().Optional());

        Assert.Equal(
            """{"type":"object","properties":{"required":{"type":"string"},"optional":{"type":["string","null"]},"choice":{"type":["string","null"],"enum":["a","b",null]},"nested":{"type":["object","null"],"additionalProperties":false}},"required":["required","optional","choice","nested"],"additionalProperties":false}""",
            Json(schema, Rewriters.RequireAllAsNullable));
    }

    [Fact]
    public void RequireAllAsNullable_wraps_references_in_anyOf()
    {
        ObjectSchema schema = Z.Object().Property("next", Z.Lazy("text", () => Z.String()).Optional());

        JsonObject result = schema.ToJsonSchema(With(Rewriters.RequireAllAsNullable));

        Assert.Equal("""{"anyOf":[{"$ref":"#/$defs/text"},{"type":"null"}]}""", result["properties"]!["next"]!.ToJsonString());
    }

    [Fact]
    public void RequireAllAsNullable_leaves_nullable_properties_alone()
    {
        ObjectSchema schema = Z.Object().Property("a", Z.AnyOf(Z.String(), Z.Null()).Optional());

        Assert.Equal(
            """{"type":"object","properties":{"a":{"anyOf":[{"type":"string"},{"type":"null"}]}},"required":["a"],"additionalProperties":false}""",
            Json(schema, Rewriters.RequireAllAsNullable));
    }

    [Fact]
    public void RequireAllAsNullable_lists_a_default_property_once_in_output_direction()
    {
        ObjectSchema schema = Z.Object().Property("limit", Z.Integer().Default(10));

        JsonObject result = schema.ToJsonSchema(With(Rewriters.RequireAllAsNullable) with { Direction = SchemaDirection.Output });

        Assert.Equal("""["limit"]""", result["required"]!.ToJsonString());
    }

    [Fact]
    public void RequireObjectRoot_keeps_an_object_root()
    {
        ObjectSchema schema = Z.Object().Property("a", Z.String());

        Assert.Equal(Json(schema), Json(schema, Rewriters.RequireObjectRoot));
    }

    [Fact]
    public void RequireObjectRoot_wraps_a_nullable_object()
    {
        Assert.Equal(
            """{"type":"object","properties":{"data":{"type":["object","null"],"additionalProperties":false}},"required":["data"],"additionalProperties":false}""",
            Json(Z.Object().Nullable(), Rewriters.RequireObjectRoot));
    }

    [Fact]
    public void RequireObjectRoot_wraps_a_union()
    {
        Assert.False(Rewriters.HasObjectRoot(Z.AnyOf(Z.Object(), Z.String()).ToNode()));
        Assert.True(Rewriters.HasObjectRoot(Z.Object().ToNode()));
    }

    [Fact]
    public void ConvertBooleanSchemas_replaces_boolean_subschemas()
    {
        JsonValueSchema schema = Z.Tuple(Z.Any(), Z.Never());

        Assert.Equal(
            """{"type":"array","prefixItems":[{},{"not":{}}],"items":{"not":{}},"minItems":2}""",
            Json(schema, Rewriters.ConvertBooleanSchemas));
    }

    [Fact]
    public void ConvertBooleanSchemas_converts_the_root()
    {
        Assert.Equal("""{"not":{}}""", Json(Z.Never(), Rewriters.ConvertBooleanSchemas));
    }

    [Fact]
    public void ConvertBooleanSchemas_keeps_boolean_additionalProperties()
    {
        ObjectSchema schema = Z.Object().AdditionalProperties(Z.Never());

        Assert.Equal("""{"type":"object","additionalProperties":false}""", Json(schema, Rewriters.ConvertBooleanSchemas));
    }

    [Fact]
    public void MoveDefaultToDescription_keeps_the_description()
    {
        EmitResult result = Z.Integer().Description("Page size.").Default(10).Emit(With(Rewriters.MoveDefaultToDescription));

        Assert.Equal("Page size. Default: 10.", result.Schema["description"]!.GetValue<string>());
        Assert.False(result.Schema.ContainsKey("default"));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Keyword == "default");
    }

    [Fact]
    public void RelocateToDescription_states_each_keyword()
    {
        StringSchema schema = Z.String().Description("A name.").MinLength(1).MaxLength(5).Pattern("^a");

        EmitResult result = schema.Emit(With(Rewriters.RelocateToDescription("minLength", "maxLength")));

        Assert.Equal(
            """{"description":"A name. Must also satisfy: minLength 1; maxLength 5.","type":"string","pattern":"^a"}""",
            result.Schema.ToJsonString());
        Assert.Equal(2, result.Diagnostics.Length);
    }

    [Fact]
    public void RelocateToDescription_writes_subschemas_as_json()
    {
        JsonObject result = Z.Not(Z.String()).ToJsonSchema(With(Rewriters.RelocateToDescription("not")));

        Assert.Equal("""Must also satisfy: not {"type":"string"}.""", result["description"]!.GetValue<string>());
    }

    [Fact]
    public void RelocateToDescription_opens_a_tuple()
    {
        JsonObject result = Z.Tuple(Z.String()).ToJsonSchema(With(Rewriters.RelocateToDescription("prefixItems")));

        Assert.False(result.ContainsKey("items"));
        Assert.False(result.ContainsKey("prefixItems"));
    }

    [Fact]
    public void RelocateToDescription_moves_extensions()
    {
        StringSchema schema = Z.String().Meta("x-order", JsonElement.Parse("1"));

        Assert.Equal(
            """{"description":"Must also satisfy: x-order 1.","type":"string"}""",
            Json(schema, Rewriters.RelocateToDescription("x-order")));
    }

    [Fact]
    public void Omit_policy_leaves_the_description_alone()
    {
        EmitResult result = Z.String().MinLength(1).Emit(With(Rewriters.RelocateToDescription("minLength")) with { Unrepresentable = UnrepresentablePolicy.Omit });

        Assert.Equal("""{"type":"string"}""", result.Schema.ToJsonString());
        Assert.Single(result.Diagnostics);
    }

    [Fact]
    public void Throw_policy_throws()
    {
        StringSchema schema = Z.String().MinLength(1);

        Assert.Throws<InvalidOperationException>(() =>
            schema.Emit(With(Rewriters.RelocateToDescription("minLength")) with { Unrepresentable = UnrepresentablePolicy.Throw }));
    }

    [Fact]
    public void InlineOrRejectRecursion_cuts_a_cycle_and_reports_it()
    {
        EmitResult result = Samples.Category.Emit(With(Rewriters.InlineOrRejectRecursion));

        Assert.False(result.Schema.ContainsKey("$defs"));
        Assert.DoesNotContain("$ref", result.Schema.ToJsonString());
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Keyword == "$ref");
    }

    [Fact]
    public void InlineOrRejectRecursion_inlines_a_plain_reference()
    {
        JsonValueSchema reference = Z.Lazy("name", () => Z.String().MinLength(1));
        ObjectSchema schema = Z.Object().Property("a", reference).Property("b", reference);

        EmitResult result = schema.Emit(With(Rewriters.InlineOrRejectRecursion));

        Assert.Equal(
            """{"type":"object","properties":{"a":{"type":"string","minLength":1},"b":{"type":"string","minLength":1}},"required":["a","b"],"additionalProperties":false}""",
            result.Schema.ToJsonString());
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void InlineOrRejectRecursion_resolves_local_definitions()
    {
        SchemaNode target = Z.String().ToNode();
        SchemaNode root = Z.Object().ToNode() with
        {
            Defs = [new SchemaProperty("a/b", target)],
            Properties = [new SchemaProperty("x", Z.Any().ToNode() with { Ref = "#/$defs/a~1b" })],
        };
        List<EmitDiagnostic> diagnostics = [];

        SchemaNode result = Rewriters.InlineOrRejectRecursion.Rewrite(root, new RewriteContext(new JsonSchemaOptions(), true, diagnostics.Add));

        Assert.Empty(diagnostics);
        Assert.True(result.Defs.IsEmpty);
        Assert.Equal(JsonTypes.String, result.Properties[0].Schema.Types);
    }

    [Fact]
    public void InlineOrRejectRecursion_reports_a_root_reference()
    {
        SchemaNode root = Z.Object().ToNode() with { Properties = [new SchemaProperty("self", Z.Any().ToNode() with { Ref = "#" })] };
        List<EmitDiagnostic> diagnostics = [];

        SchemaNode result = Rewriters.InlineOrRejectRecursion.Rewrite(root, new RewriteContext(new JsonSchemaOptions(), true, diagnostics.Add));

        Assert.Null(result.Properties[0].Schema.Ref);
        Assert.Single(diagnostics);
    }

    [Fact]
    public void InlineOrRejectRecursion_reports_an_external_reference()
    {
        SchemaNode root = Z.Object().ToNode() with { Properties = [new SchemaProperty("x", Z.Any().ToNode() with { Ref = "https://example.com/a" })] };
        List<EmitDiagnostic> diagnostics = [];

        SchemaNode result = Rewriters.InlineOrRejectRecursion.Rewrite(root, new RewriteContext(new JsonSchemaOptions(), true, diagnostics.Add));

        Assert.Equal("https://example.com/a", result.Properties[0].Schema.Ref);
        Assert.Single(diagnostics);
    }

    [Fact]
    public void AcceptsNull_follows_type_keywords()
    {
        Assert.True(Rewriters.AcceptsNull(Z.String().Nullable().ToNode()));
        Assert.True(Rewriters.AcceptsNull(Z.Any().ToNode()));
        Assert.True(Rewriters.AcceptsNull(Z.AnyOf(Z.String(), Z.Null()).ToNode()));
        Assert.False(Rewriters.AcceptsNull(Z.String().ToNode()));
        Assert.False(Rewriters.AcceptsNull(Z.String().Enum("a").ToNode()));
        Assert.False(Rewriters.AcceptsNull(Z.Never().ToNode()));
        Assert.False(Rewriters.AcceptsNull(Z.AnyOf(Z.String(), Z.Integer()).ToNode()));
    }

    [Fact]
    public void Emit_result_records_its_target()
    {
        Assert.Same(LlmProfiles.GeminiOpenApi, Z.String().Emit(LlmProfiles.GeminiOpenApi).Target);
    }
}
