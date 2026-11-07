using System;
using System.Collections.Generic;
using System.Linq;
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
        ObjectSchema schema = Z.Object().Strict()
            .Property("required", Z.String())
            .Property("optional", Z.String().Optional())
            .Property("choice", Z.String().Enum("a", "b").Optional())
            .Property("nested", Z.Object().Strict().Optional());

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
    public void CloseAllObjects_makes_one_object_of_an_allOf()
    {
        ObjectSchema a = Z.Object().Property("a", Z.Integer());
        ObjectSchema b = Z.Object().Property("b", Z.String().Optional());

        Assert.Equal(
            """{"type":"object","properties":{"a":{"type":"integer","format":"int64"},"b":{"type":"string"}},"required":["a"],"additionalProperties":false}""",
            Json(Z.AllOf(a, b), Rewriters.CloseAllObjects));
    }

    [Fact]
    public void CloseAllObjects_reports_an_allOf_it_can_not_make_one_object_of()
    {
        ObjectSchema a = Z.Object().Property("a", Z.Integer());
        ObjectSchema other = Z.Object().Property("a", Z.String());

        Assert.Contains(Z.AllOf(a, other).Emit(With(Rewriters.CloseAllObjects)).Diagnostics, diagnostic => diagnostic.Keyword == "allOf");
    }

    [Fact]
    public void CloseAllObjects_keeps_an_allOf_with_rules_on_a_branch_and_reports_it()
    {
        ObjectSchema a = Z.Object().Property("a", Z.Integer());
        ObjectSchema b = Z.Object().Property("b", Z.String());

        foreach (JsonValueSchema schema in new[] { Z.AllOf(a.Refine(_ => true, "r"), b), Z.AllOf(a.Describe("d"), b) })
        {
            Assert.Contains(schema.Emit(With(Rewriters.CloseAllObjects)).Diagnostics, diagnostic => diagnostic.Keyword == "allOf");
        }
    }

    [Fact]
    public void CloseAllObjects_keeps_to_what_every_branch_allows()
    {
        const string Document = """{"type":["object","null"],"allOf":[{"type":"object","properties":{"a":{"type":"string"}}},{"type":"object","properties":{"b":{"type":"string"}}}]}""";

        Assert.DoesNotContain("null", Json(Z.FromJsonSchema(Document), Rewriters.CloseAllObjects), StringComparison.Ordinal);
    }

    [Fact]
    public void A_branch_left_open_by_patternProperties_is_still_a_match()
    {
        ObjectSchema schema = Z.Object().Property("r", Z.AnyOf(
            Z.Object().Property("o", Z.String().Optional()).PatternProperty("^x", Z.String()),
            Z.Object().Property("k", Z.String())));

        ParseResult<JsonElement> result = schema.ParseModelOutput(schema.Emit(LlmProfiles.OpenAIStrict), """{"r":{"o":null,"x1":"v"}}""");

        Assert.Equal("""{"r":{"x1":"v"}}""", result.Value.GetRawText());
    }

    [Fact]
    public void A_pattern_branch_takes_only_the_names_of_its_pattern()
    {
        ObjectSchema schema = Z.Object().Property("r", Z.AnyOf(
            Z.Object().Property("id", Z.Integer()).PatternProperty("^x-", Z.String()),
            Z.Object().Property("id", Z.String()).Property("note", Z.String().Optional())));

        ParseResult<JsonElement> result = schema.ParseModelOutput(schema.Emit(LlmProfiles.OpenAIStrict), """{"r":{"id":"abc","note":null}}""");

        Assert.Equal("""{"r":{"id":"abc"}}""", result.Value.GetRawText());
    }

    [Fact]
    public void A_union_of_arrays_is_told_apart_by_its_items()
    {
        ObjectSchema schema = Z.Object().Property("r", Z.AnyOf(
            Z.Array(Z.String()),
            Z.Array(Z.Object().Property("id", Z.String()).Property("note", Z.String().Optional()))));

        ParseResult<JsonElement> result = schema.ParseModelOutput(schema.Emit(LlmProfiles.OpenAIStrict), """{"r":[{"id":"a","note":null}]}""");

        Assert.Equal("""{"r":[{"id":"a"}]}""", result.Value.GetRawText());
    }

    private static readonly ObjectSchema Item = Z.Object().Property("id", Z.String()).Property("note", Z.String().Optional());

    [Fact]
    public void A_union_of_arrays_of_a_named_item_is_told_apart_too()
    {
        ObjectSchema schema = Z.Object().Property("r", Z.AnyOf(Z.Array(Z.String()), Z.Array(Z.Lazy("item", () => Item))));

        ParseResult<JsonElement> result = schema.ParseModelOutput(schema.Emit(LlmProfiles.OpenAIStrict), """{"r":[{"id":"a","note":null}]}""");

        Assert.Equal("""{"r":[{"id":"a"}]}""", result.Value.GetRawText());
    }

    [Fact]
    public void A_named_object_root_is_not_unwrapped_when_inlining_left_it_an_object()
    {
        JsonValueSchema schema = Z.Lazy("thing", () => Z.Object().Property("data", Z.String()));

        ParseResult<JsonElement> result = schema.ParseModelOutput(schema.Emit(LlmProfiles.AnthropicStrict), """{"data":"x"}""");

        Assert.Equal("""{"data":"x"}""", result.Value.GetRawText());
    }

    [Fact]
    public void A_coerced_property_sent_as_text_fits_its_branch()
    {
        ObjectSchema schema = Z.Object().Property("item", Z.AnyOf(
            Z.Object().Property("on", Z.Boolean().Coerce()).Property("note", Z.String().Optional()),
            Z.String()));

        ParseResult<JsonElement> result = schema.ParseModelOutput(schema.Emit(LlmProfiles.OpenAIStrict), """{"item":{"on":"true","note":null}}""");

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void A_long_list_of_unions_is_adapted_to_the_end()
    {
        ObjectSchema schema = Z.Object().Property("rs", Z.Array(Z.AnyOf(Z.Array(Z.String()), Z.Array(Item))));
        string items = string.Join(',', Enumerable.Repeat("""[{"id":"a","note":null}]""", 2_000));

        ParseResult<JsonElement> result = schema.ParseModelOutput(schema.Emit(LlmProfiles.OpenAIStrict), $$"""{"rs":[{{items}}]}""");

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain("note", result.Value.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_refinement_in_a_branch_sees_only_the_adapted_value_and_runs_once()
    {
        int runs = 0;
        ObjectSchema branch = Z.Object().Property("id", Z.String()).Property("n", Z.Integer().Optional())
            .Refine(value => ++runs > 0 && (!value.TryGetProperty("n", out JsonElement n) || n.GetInt64() >= 0), "negative");
        ObjectSchema schema = Z.Object().Property("r", Z.AnyOf(branch, Z.Object().Property("k", Z.Boolean())));

        ParseResult<JsonElement> result = schema.ParseModelOutput(schema.Emit(LlmProfiles.OpenAIStrict), """{"r":{"id":"a","n":null}}""");

        Assert.Equal("""{"r":{"id":"a"}}""", result.Value.GetRawText());
        Assert.Equal(1, runs);
    }

    [Fact]
    public void Branches_that_differ_inside_a_nested_object_are_told_apart()
    {
        ObjectSchema first = Z.Object().Property("data", Z.Object().Property("x", Z.String()).Property("p", Z.String().Optional()));
        ObjectSchema second = Z.Object().Property("data", Z.Object().Property("y", Z.Boolean()).Property("q", Z.String().Optional()));
        ObjectSchema schema = Z.Object().Property("r", Z.AnyOf(first, second));

        ParseResult<JsonElement> result = schema.ParseModelOutput(schema.Emit(LlmProfiles.OpenAIStrict), """{"r":{"data":{"y":true,"q":null}}}""");

        Assert.Equal("""{"r":{"data":{"y":true}}}""", result.Value.GetRawText());
    }

    [Fact]
    public void CloseAllObjects_reports_an_object_beside_a_union_of_objects()
    {
        JsonValueSchema schema = Z.AllOf(
            Z.Object().Property("id", Z.String()),
            Z.AnyOf(Z.Object().Property("a", Z.String()), Z.Object().Property("b", Z.Boolean())));

        Assert.Contains(schema.Emit(With(Rewriters.CloseAllObjects)).Diagnostics, diagnostic => diagnostic.Keyword == "allOf");
    }

    [Fact]
    public void A_pattern_branch_the_profile_leaves_open_takes_other_names()
    {
        ObjectSchema schema = Z.Object().Property("r", Z.AnyOf(
            Z.Object().Property("id", Z.Integer()).Property("note", Z.String().Optional()).PatternProperty("^x-", Z.String()),
            Z.Object().Property("k", Z.String())));

        ParseResult<JsonElement> result = schema.ParseModelOutput(schema.Emit(LlmProfiles.OpenAIStrict), """{"r":{"id":1,"note":null,"other":5}}""");

        Assert.True(result.IsSuccess);
        Assert.Equal("""{"r":{"id":1}}""", result.Value.GetRawText());
    }

    [Fact]
    public void A_profile_that_does_not_close_objects_leaves_branches_open()
    {
        ObjectSchema schema = Z.Object().Property("r", Z.AnyOf(Z.Object().Property("id", Z.String()).Property("note", Z.String().Optional())));
        ParseResult<JsonElement> result = schema.ParseModelOutput(schema.Emit(With(Rewriters.RequireAllAsNullable)), """{"r":{"id":"a","note":null,"extra":1}}""");

        Assert.True(result.IsSuccess);
        Assert.Equal("""{"r":{"id":"a"}}""", result.Value.GetRawText());
    }

    [Fact]
    public void A_passthrough_branch_is_closed_when_the_profile_closes_it_and_types_count()
    {
        ObjectSchema schema = Z.Object().Property("r", Z.AnyOf(
            Z.Object().Property("id", Z.Integer()).Passthrough(),
            Z.Object().Property("id", Z.String()).Property("note", Z.String().Optional())));

        ParseResult<JsonElement> result = schema.ParseModelOutput(schema.Emit(LlmProfiles.OpenAIStrict), """{"r":{"id":"abc","note":null}}""");

        Assert.True(result.IsSuccess);
        Assert.Equal("""{"r":{"id":"abc"}}""", result.Value.GetRawText());
    }

    [Fact]
    public void An_allOf_inside_an_allOf_counts_too()
    {
        ObjectSchema x = Z.Object().Property("x", Z.String());
        ObjectSchema y = Z.Object().Property("y", Z.String());
        ObjectSchema optional = Z.Object().Property("o", Z.String().Optional());
        ObjectSchema c = Z.Object().Property("c1", Z.String()).Property("c2", Z.String().Optional());
        ObjectSchema schema = Z.Object().Property("r", Z.AnyOf(Z.AllOf(Z.AllOf(x, y), optional), c));

        ParseResult<JsonElement> result = schema.ParseModelOutput(schema.Emit(LlmProfiles.OpenAIStrict), """{"r":{"c1":"1","c2":null}}""");

        Assert.Equal("""{"r":{"c1":"1"}}""", result.Value.GetRawText());
    }

    [Fact]
    public void An_allOf_in_a_union_matches_what_its_branches_say_together()
    {
        ObjectSchema x = Z.Object().Property("x", Z.String());
        ObjectSchema y = Z.Object().Property("y", Z.String());
        ObjectSchema c = Z.Object().Property("c1", Z.String()).Property("c2", Z.String().Optional());
        ObjectSchema schema = Z.Object().Property("r", Z.AnyOf(Z.AllOf(x, y), c));

        ParseResult<JsonElement> result = schema.ParseModelOutput(schema.Emit(LlmProfiles.OpenAIStrict), """{"r":{"c1":"v","c2":null}}""");

        Assert.Equal("""{"r":{"c1":"v"}}""", result.Value.GetRawText());
    }

    [Fact]
    public void Model_output_never_silently_loses_a_property_of_a_branch()
    {
        ObjectSchema basic = Z.Object().Property("a", Z.Integer());
        ObjectSchema extended = Z.Object().Property("a", Z.Integer()).Property("c", Z.String().Optional()).Property("d", Z.String().Optional());
        ObjectSchema open = Z.Object().Property("r", Z.OneOf(basic, extended));
        ObjectSchema strict = Z.Object().Property("r", Z.OneOf(basic.Strict(), extended.Strict()));
        const string Text = """{"r":{"a":1,"c":"x","d":null}}""";

        Assert.False(open.ParseModelOutput(open.Emit(LlmProfiles.OpenAIStrict), Text).IsSuccess);
        Assert.Equal("""{"r":{"a":1,"c":"x"}}""", strict.ParseModelOutput(strict.Emit(LlmProfiles.OpenAIStrict), Text).Value.GetRawText());
    }

    [Fact]
    public void RequireAllAsNullable_leaves_nullable_properties_alone()
    {
        ObjectSchema schema = Z.Object().Strict().Property("a", Z.AnyOf(Z.String(), Z.Null()).Optional());

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
            Json(Z.Object().Strict().Nullable(), Rewriters.RequireObjectRoot));
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
        ObjectSchema schema = Z.Object().Strict().Property("a", reference).Property("b", reference);

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
