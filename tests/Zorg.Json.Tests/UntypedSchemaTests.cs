using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Zorg.Json.Emission;

namespace Zorg.Json.Tests;

public sealed class UntypedSchemaTests
{
    private static readonly ObjectSchema Search = Z.Object()
        .Property("query", Z.String().Trim().MinLength(1))
        .Property("limit", Z.Integer().Minimum(1).Maximum(50).Default(10))
        .Property("since", Z.String().Date().Optional());

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static ParseIssue SingleIssue<T>(ParseResult<T> result) => Assert.Single(result.Issues);

    [Theory]
    [InlineData("1", 1L)]
    [InlineData("1.0", 1L)]
    [InlineData("1e2", 100L)]
    [InlineData("-3", -3L)]
    public void Integer_follows_json_schema(string json, long expected) => Assert.Equal(expected, Z.Integer().Parse(json));

    [Theory]
    [InlineData("1.5")]
    [InlineData("\"1\"")]
    [InlineData("true")]
    [InlineData("9223372036854775808")]
    public void Integer_rejects_everything_else(string json) => Assert.False(Z.Integer().SafeParse(json).IsSuccess);

    [Theory]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData("5", true)]
    [InlineData("6", false)]
    public void Number_limits_are_inclusive(string json, bool valid) =>
        Assert.Equal(valid, Z.Number().Minimum(1).Maximum(5).SafeParse(json).IsSuccess);

    [Theory]
    [InlineData("1", false)]
    [InlineData("2", true)]
    [InlineData("4", true)]
    [InlineData("5", false)]
    public void Exclusive_limits_are_exclusive(string json, bool valid) =>
        Assert.Equal(valid, Z.Number().ExclusiveMinimum(1).ExclusiveMaximum(5).SafeParse(json).IsSuccess);

    [Fact]
    public void Limits_compare_exactly_beyond_double_precision()
    {
        Assert.False(Z.Number<decimal>().Maximum(9007199254740992m).SafeParse("9007199254740993").IsSuccess);
        Assert.True(Z.Number<decimal>().Maximum(9007199254740992m).SafeParse("9007199254740992").IsSuccess);
    }

    [Theory]
    [InlineData("0.3", true)]
    [InlineData("0.35", false)]
    [InlineData("1e308", true)]
    public void MultipleOf_is_exact(string json, bool valid) =>
        Assert.Equal(valid, Z.Number<double>().MultipleOf(0.1).SafeParse(json).IsSuccess);

    [Fact]
    public void Number_shortcuts_and_guards()
    {
        Assert.False(Z.Number().Positive().SafeParse("0").IsSuccess);
        Assert.False(Z.Number().Negative().SafeParse("0").IsSuccess);
        Assert.True(Z.Integer().Enum(1, 2).SafeParse("2").IsSuccess);
        Assert.False(Z.Integer().Const(1).SafeParse("2").IsSuccess);
        Assert.Throws<ArgumentOutOfRangeException>(() => Z.Integer().MultipleOf(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Z.Number().Minimum(double.NaN));
    }

    [Fact]
    public void Numeric_types_enforce_their_range()
    {
        Assert.Equal((byte)255, Z.Number<byte>().Parse("255"));
        Assert.Equal(IssueCodes.InvalidType, SingleIssue(Z.Number<byte>().SafeParse("256")).Code);
        Assert.Equal(10.5m, Z.Number<decimal>().Parse("10.5"));
        Assert.Equal(IssueCodes.InvalidType, SingleIssue(Z.Number().SafeParse("1e400")).Code);
    }

    [Fact]
    public void Nullable_number_returns_null() =>
        Assert.Null(Z.Number<int>().Minimum(1).Nullable().Parse("null"));

    [Fact]
    public void Nullable_number_keeps_constraints()
    {
        Assert.Equal(5, Z.Number<int>().Minimum(1).Nullable().Parse("5"));
        Assert.False(Z.Number<int>().Minimum(1).Nullable().SafeParse("0").IsSuccess);
    }

    [Fact]
    public void Nullable_string_returns_null_and_adds_null_to_enum()
    {
        Assert.Null(Z.String().Nullable().Parse("null"));
        Assert.True(Z.String().Enum("a").Nullable().SafeParse("null").IsSuccess);
        Assert.True(Z.String().Nullable().Enum("a").SafeParse("null").IsSuccess);
        Assert.False(Z.String().Const("a").Nullable().SafeParse("\"b\"").IsSuccess);
    }

    [Fact]
    public void Coerce_accepts_numeric_strings()
    {
        Assert.Equal(42L, Z.Integer().Coerce().Parse("\"42\""));
        Assert.Equal(42L, Z.Integer().Coerce().Parse("42"));
        Assert.Equal(IssueCodes.InvalidType, SingleIssue(Z.Integer().Coerce().SafeParse("\"abc\"")).Code);
        Assert.Equal(IssueCodes.TooSmall, SingleIssue(Z.Integer().Coerce().Minimum(50).SafeParse("\"42\"")).Code);
    }

    [Fact]
    public void Coerce_accepts_boolean_strings()
    {
        Assert.True(Z.Boolean().Coerce().Parse("\"true\""));
        Assert.False(Z.Boolean().Coerce().SafeParse("\"yes\"").IsSuccess);
        Assert.True(Z.Boolean().Parse("true"));
        Assert.False(Z.Boolean().Const(true).SafeParse("false").IsSuccess);
    }

    [Fact]
    public void Arrays_parse_items_and_report_indexes()
    {
        ArraySchema<long> schema = Z.Array(Z.Integer()).MinItems(1).MaxItems(2);

        Assert.Equal([1L, 2L], schema.Parse("[1, 2]"));
        Assert.Equal(IssueCodes.TooSmall, SingleIssue(schema.SafeParse("[]")).Code);
        Assert.Equal(IssueCodes.TooBig, SingleIssue(schema.SafeParse("[1,2,3]")).Code);
        Assert.Equal("/1", SingleIssue(schema.SafeParse("""[1, "x"]""")).InstanceLocation);
        Assert.Equal(IssueCodes.InvalidType, SingleIssue(schema.SafeParse("1")).Code);
    }

    [Fact]
    public void Array_keywords()
    {
        Assert.Equal("uniqueItems", SingleIssue(Z.Array(Z.Integer()).UniqueItems().SafeParse("[1, 1.0]")).Keyword);
        Assert.False(Z.Array().NonEmpty().SafeParse("[]").IsSuccess);
        Assert.True(Z.Array().Contains(Z.String()).SafeParse("""[1, "a"]""").IsSuccess);
        Assert.Equal("contains", SingleIssue(Z.Array().Contains(Z.String()).SafeParse("[1]")).Keyword);
        Assert.Equal("maxContains", SingleIssue(Z.Array().Contains(Z.String(), maximum: 1).SafeParse("""["a", "b"]""")).Keyword);
        Assert.True(Z.Array(Z.String()).Nullable().SafeParse("null").IsSuccess);
    }

    [Fact]
    public void Normalizing_items_rewrites_the_array() =>
        Assert.Equal(["a", "b"], Z.Array(Z.String().Trim()).Parse("""["a", " b "]"""));

    [Fact]
    public void Tuple_fixes_positions()
    {
        JsonValueSchema point = Z.Tuple(Z.Number(), Z.String());

        Assert.True(point.SafeParse("""[1, "a"]""").IsSuccess);
        Assert.False(point.SafeParse("""["a", 1]""").IsSuccess);
        Assert.False(point.SafeParse("""[1, "a", 2]""").IsSuccess);
        Assert.False(point.SafeParse("[1]").IsSuccess);
    }

    [Fact]
    public void Object_applies_defaults_and_normalization()
    {
        JsonElement result = Search.Parse("""{ "query": "  hi  " }""");

        Assert.Equal("hi", result.GetProperty("query").GetString());
        Assert.Equal(10, result.GetProperty("limit").GetInt32());
    }

    [Fact]
    public void Object_reports_each_problem()
    {
        ParseResult<JsonElement> result = Search.SafeParse("""{ "limit": 0, "unknown": 1, "other": 2 }""");

        Assert.Equal(3, result.Issues.Length);
        Assert.Contains(result.Issues, issue => issue is { Code: IssueCodes.Required, InstanceLocation: "/query" });
        Assert.Contains(result.Issues, issue => issue is { Code: IssueCodes.TooSmall, InstanceLocation: "/limit" });
        Assert.Contains(result.Issues, issue => issue is { Code: IssueCodes.UnrecognizedKeys, InstanceLocation: "" });
    }

    [Fact]
    public void Json_pointer_escapes_names() =>
        Assert.Equal("/a~1b~0c", SingleIssue(Z.Object().Property("a/b~c", Z.String()).SafeParse("""{ "a/b~c": 1 }""")).InstanceLocation);

    [Fact]
    public void Unknown_property_modes()
    {
        ObjectSchema schema = Z.Object().Property("a", Z.Integer());
        const string Json = """{ "b": 2, "a": 1, "c": 3 }""";

        Assert.False(schema.Strict().SafeParse(Json).IsSuccess);
        Assert.Equal("""{"a":1}""", schema.Strip().Parse(Json).GetRawText());
        Assert.Equal(Json, schema.Passthrough().Parse(Json).GetRawText());
        Assert.False(schema.AdditionalProperties(Z.String()).SafeParse(Json).IsSuccess);
        Assert.True(schema.AdditionalProperties(Z.Integer()).SafeParse(Json).IsSuccess);
    }

    [Fact]
    public void Object_keywords()
    {
        ObjectSchema open = Z.Object().Passthrough();

        Assert.Equal("minProperties", SingleIssue(open.MinProperties(1).SafeParse("{}")).Keyword);
        Assert.Equal("maxProperties", SingleIssue(open.MaxProperties(0).SafeParse("""{ "a": 1 }""")).Keyword);
        Assert.False(open.PropertyNames(Z.String().MaxLength(1)).SafeParse("""{ "ab": 1 }""").IsSuccess);
        Assert.False(open.PatternProperty("^x-", Z.String()).SafeParse("""{ "x-a": 1 }""").IsSuccess);
        Assert.True(Z.Object().PatternProperty("^x-", Z.String()).SafeParse("""{ "x-a": "b" }""").IsSuccess);

        ParseIssue dependent = SingleIssue(open.DependentRequired("card", "address").SafeParse("""{ "card": 1 }"""));
        Assert.Equal(IssueCodes.Required, dependent.Code);
        Assert.Equal("/address", dependent.InstanceLocation);
        Assert.True(Z.Object().Nullable().SafeParse("null").IsSuccess);
        Assert.True(Z.Record(Z.Integer()).SafeParse("""{ "a": 1 }""").IsSuccess);
    }

    [Fact]
    public void Shape_algebra()
    {
        ObjectSchema user = Z.Object().Property("id", Z.Integer()).Property("name", Z.String());

        Assert.True(user.Pick("name").SafeParse("""{ "name": "a" }""").IsSuccess);
        Assert.True(user.Omit("id").SafeParse("""{ "name": "a" }""").IsSuccess);
        Assert.True(user.Partial().SafeParse("{}").IsSuccess);

        ObjectSchema extended = user.Extend(Z.Object().Property("name", Z.Integer()).Property("role", Z.String().Optional()));
        Assert.True(extended.SafeParse("""{ "id": 1, "name": 2 }""").IsSuccess);
        Assert.Single(extended.ToNode().Properties, property => property.Name == "name");
    }

    [Fact]
    public void Property_can_be_replaced_and_become_optional()
    {
        ObjectSchema schema = Z.Object().Property("a", Z.String()).Property("a", Z.String().Optional());

        Assert.True(schema.SafeParse("{}").IsSuccess);
        Assert.Single(schema.ToNode().Properties);
    }

    [Fact]
    public void Unions()
    {
        JsonValueSchema any = Z.AnyOf(Z.String(), Z.Integer());
        Assert.True(any.SafeParse("1").IsSuccess);
        Assert.Equal(IssueCodes.InvalidUnion, SingleIssue(any.SafeParse("true")).Code);

        JsonValueSchema one = Z.OneOf(Z.Integer(), Z.Number());
        Assert.True(one.SafeParse("1.5").IsSuccess);
        Assert.Equal(IssueCodes.InvalidUnion, SingleIssue(one.SafeParse("1")).Code);
        Assert.Equal(IssueCodes.InvalidUnion, SingleIssue(one.SafeParse("true")).Code);

        Assert.False(Z.AllOf(Z.String().MinLength(1), Z.String().MaxLength(1)).SafeParse("\"ab\"").IsSuccess);
        Assert.False(Z.Not(Z.Null()).SafeParse("null").IsSuccess);
        Assert.True(Z.Any().SafeParse("[1]").IsSuccess);
        Assert.False(Z.Never().SafeParse("1").IsSuccess);
        Assert.True(Z.Null().SafeParse("null").IsSuccess);
    }

    [Fact]
    public void Union_branch_keeps_its_normalization() =>
        Assert.Equal("a", Z.AnyOf(Z.Integer(), Z.String().Trim()).Parse("\" a \"").GetString());

    [Fact]
    public void If_selects_a_branch_and_hides_its_own_issues()
    {
        JsonValueSchema schema = Z.If(Z.String(), Z.String().MinLength(2), Z.Integer());

        Assert.True(schema.SafeParse("\"ab\"").IsSuccess);
        Assert.Equal(IssueCodes.TooSmall, SingleIssue(schema.SafeParse("\"a\"")).Code);
        Assert.Equal(IssueCodes.InvalidType, SingleIssue(schema.SafeParse("true")).Code);
    }

    [Fact]
    public void Discriminated_union_reports_only_the_selected_branch()
    {
        JsonValueSchema shape = Z.DiscriminatedUnion(
            "kind",
            Z.Object().Property("kind", Z.String().Const("circle")).Property("radius", Z.Number()),
            Z.Object().Property("kind", Z.String().Enum("square", "box")).Property("side", Z.Number()));

        Assert.True(shape.SafeParse("""{ "kind": "box", "side": 1 }""").IsSuccess);
        Assert.Equal("/radius", SingleIssue(shape.SafeParse("""{ "kind": "circle" }""")).InstanceLocation);
        Assert.Equal("/kind", SingleIssue(shape.SafeParse("{}")).InstanceLocation);
    }

    [Fact]
    public void Lazy_allows_recursion()
    {
        Assert.True(Tree.Schema.SafeParse("""{ "value": 1, "children": [{ "value": 2, "children": [] }] }""").IsSuccess);
        Assert.Equal("/children/0/value", SingleIssue(Tree.Schema.SafeParse("""{ "value": 1, "children": [{ "value": "x", "children": [] }] }""")).InstanceLocation);
        Snapshot.Match(Tree.Schema.ToJsonSchema().ToJsonString(Indented));
    }

    [Fact]
    public void Lazy_that_consumes_no_input_fails_cleanly()
    {
        JsonValueSchema loop = Z.Lazy("loop", () => Loop.Schema);

        Assert.Equal(IssueCodes.InvalidValue, loop.SafeParse("1").Issues[0].Code);
    }

    [Fact]
    public async Task Nested_async_refinements_run_in_SafeParseAsync()
    {
        ObjectSchema schema = Z.Object().Property("name", Z.String().RefineAsync(IsFree, "Taken"));

        ParseResult<JsonElement> result = await schema.SafeParseAsync(JsonElement.Parse("""{ "name": "ada" }"""), TestContext.Current.CancellationToken);

        Assert.Equal("/name", Assert.Single(result.Issues).InstanceLocation);
        Assert.True((await schema.SafeParseAsync(JsonElement.Parse("""{ "name": "bob" }"""), TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Throws<InvalidOperationException>(() => schema.SafeParse("""{ "name": "bob" }"""));
    }

    private static ValueTask<bool> IsFree(string name, CancellationToken cancellationToken) => ValueTask.FromResult(name != "ada");

    [Fact]
    public void Nested_refinements_run_with_paths()
    {
        ObjectSchema schema = Z.Object().Property("a", Z.Array(Z.Integer().Refine(number => number != 3, "No threes")));

        Assert.Equal("/a/1", SingleIssue(schema.SafeParse("""{ "a": [1, 3] }""")).InstanceLocation);
    }

    [Fact]
    public async Task Parsed_elements_outlive_the_stream()
    {
        using System.IO.MemoryStream stream = new(Encoding.UTF8.GetBytes("""{ "a": 1 }"""));

        JsonElement result = (await Z.Object().Passthrough().SafeParseAsync(stream, TestContext.Current.CancellationToken)).Value;

        Assert.Equal(1, result.GetProperty("a").GetInt32());
    }

    [Fact]
    public void Metadata_is_emitted_and_kept_through_a_chain()
    {
        StringSchema schema = Z.String()
            .Title("T").Describe("D").Comment("C").Deprecated().ReadOnly().WriteOnly().Examples("x", "y")
            .Meta("x-order", JsonElement.Parse("1"))
            .MinLength(1);

        Snapshot.Match(schema.ToJsonSchema().ToJsonString(Indented));
    }

    [Fact]
    public void Object_schema_snapshot() => Snapshot.Match(Search.ToJsonSchema().ToJsonString(Indented));

    [Fact]
    public void Output_requires_defaulted_properties()
    {
        JsonArray required = Search.ToJsonSchema(new() { Direction = SchemaDirection.Output })["required"]!.AsArray();

        Assert.Contains("limit", required.Select(name => name!.GetValue<string>()));
    }

    [Fact]
    public void Strip_is_open_on_input_and_closed_on_output()
    {
        ObjectSchema schema = Z.Object().Strip();

        Assert.False(schema.ToJsonSchema().ContainsKey("additionalProperties"));
        Assert.False(schema.ToJsonSchema(new() { Direction = SchemaDirection.Output })["additionalProperties"]!.GetValue<bool>());
        Assert.False(Z.Object().Passthrough().ToJsonSchema().ContainsKey("additionalProperties"));
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void Every_target_emits_a_kitchen_sink(string name, JsonSchemaProfile target)
    {
        ObjectSchema schema = Z.Object()
            .Property("nullable", Z.String().Nullable())
            .Property("mixed", Z.AnyOf(Z.String(), Z.Integer()))
            .Property("number", Z.Number().ExclusiveMinimum(0).Maximum(5).Examples(1))
            .Property("constant", Z.String().Const("a"))
            .Property("tuple", Z.Tuple(Z.String(), Z.Integer()))
            .Property("tree", Tree.Schema)
            .PatternProperty("^x-", Z.String())
            .DependentRequired("constant", "number");

        EmitResult result = schema.Emit(new() { Target = target, Unrepresentable = UnrepresentablePolicy.Omit });

        Snapshot.Match(result.Schema.ToJsonString(Indented), testName: $"{nameof(Every_target_emits_a_kitchen_sink)}_{name}");
    }

    public static TheoryData<string, JsonSchemaProfile> Targets { get; } = new()
    {
        { "draft202012", JsonSchemaProfile.Draft202012 },
        { "draft07", JsonSchemaProfile.Draft07 },
        { "openapi30", JsonSchemaProfile.OpenApi30 },
        { "openapi31", JsonSchemaProfile.OpenApi31 },
    };

    [Fact]
    public void Coerce_is_visible_only_in_the_input_schema()
    {
        JsonObject input = Z.Integer().Coerce().ToJsonSchema();
        JsonObject output = Z.Integer().Coerce().ToJsonSchema(new() { Direction = SchemaDirection.Output });

        Assert.Equal(2, input["type"]!.AsArray().Count);
        Assert.True(input.ContainsKey("pattern"));
        Assert.Equal("integer", output["type"]!.GetValue<string>());
    }

    [Fact]
    public void Coerce_leaves_const_out_of_the_input_schema()
    {
        EmitResult result = Z.Integer().Coerce().Const(5).Emit();

        Assert.False(result.Schema.ContainsKey("const"));
        Assert.Equal("const", Assert.Single(result.Diagnostics).Keyword);
    }

    [Fact]
    public void Named_schemas_are_hoisted_on_request()
    {
        StringSchema email = Z.String().Email().Named("email");
        ObjectSchema schema = Z.Object().Property("a", email).Property("b", email);
        JsonSchemaOptions options = new() { Reused = ReusedSchemaHandling.Ref };

        Assert.Equal("email", schema.ToJsonSchema()["properties"]!["a"]!["format"]!.GetValue<string>());
        Assert.Equal("#/$defs/email", schema.ToJsonSchema(options)["properties"]!["b"]!["$ref"]!.GetValue<string>());
        Assert.Single(schema.ToJsonSchema(options)["$defs"]!.AsObject());
    }

    [Fact]
    public void Reference_prefix_moves_definitions_out()
    {
        EmitResult result = Z.Object().Property("a", Z.String().Named("name"))
            .Emit(new() { Reused = ReusedSchemaHandling.Ref, ReferencePrefix = "#/components/schemas/", Target = JsonSchemaProfile.OpenApi31 });

        Assert.Equal("#/components/schemas/name", result.Schema["properties"]!["a"]!["$ref"]!.GetValue<string>());
        Assert.False(result.Schema.ContainsKey("$defs"));
        Assert.Equal("name", Assert.Single(result.Definitions).Key);
    }

    [Fact]
    public void Rewriters_and_post_processors_run()
    {
        JsonSchemaProfile profile = JsonSchemaProfile.Draft202012
            .With(new DropMinLength())
            .With(new AddMarker());

        JsonObject schema = Z.Object().Property("a", Z.String().MinLength(1)).ToJsonSchema(new() { Target = profile });

        Assert.False(schema["properties"]!["a"]!.AsObject().ContainsKey("minLength"));
        Assert.True(schema["x-marked"]!.GetValue<bool>());
    }

    private sealed class DropMinLength : ISchemaRewriter
    {
        public Model.SchemaNode Rewrite(Model.SchemaNode node, RewriteContext context) => node with { MinLength = null };
    }

    private sealed class AddMarker : IJsonSchemaPostProcessor
    {
        public void Process(JsonObject schema, EmitContext context) => schema["x-marked"] = true;
    }

    private static class Tree
    {
        public static readonly ObjectSchema Schema = Z.Object()
            .Property("value", Z.Integer())
            .Property("children", Z.Array(Z.Lazy("tree", () => Schema!)));
    }

    private static class Loop
    {
        public static readonly JsonValueSchema Schema = Z.Lazy("loop", () => Schema!);
    }
}
