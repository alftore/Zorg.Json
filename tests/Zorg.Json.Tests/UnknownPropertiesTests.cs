using Xunit;
using Zorg.Json.Emission;

namespace Zorg.Json.Tests;

public sealed class UnknownPropertiesTests
{
    private static readonly ObjectSchema A = Z.Object().Property("a", Z.Integer());
    private static readonly ObjectSchema B = Z.Object().Property("b", Z.String());

    [Fact]
    public void They_are_removed_by_default()
    {
        Assert.Equal("""{"a":1}""", A.Parse("""{"a":1,"x":2}""").GetRawText());
    }

    [Fact]
    public void Both_schemas_leave_the_keyword_out()
    {
        Assert.Null(A.ToJsonSchema()["additionalProperties"]);
        Assert.Null(A.ToJsonSchema(new() { Direction = SchemaDirection.Output })["additionalProperties"]);
    }

    [Fact]
    public void Strict_rejects_them_and_closes_both_directions()
    {
        ObjectSchema strict = A.Strict();

        Assert.Equal(IssueCodes.UnrecognizedKeys, Assert.Single(strict.SafeParse("""{"a":1,"x":2}""").Issues).Code);
        Assert.False(strict.ToJsonSchema()["additionalProperties"]!.GetValue<bool>());
        Assert.False(strict.ToJsonSchema(new() { Direction = SchemaDirection.Output })["additionalProperties"]!.GetValue<bool>());
    }

    [Fact]
    public void Strict_is_for_one_object_and_not_those_inside_it()
    {
        ObjectSchema outer = Z.Object().Strict().Property("in", A);

        Assert.True(outer.SafeParse("""{"in":{"a":1,"x":2}}""").IsSuccess);
        Assert.False(outer.SafeParse("""{"in":{"a":1},"x":2}""").IsSuccess);
    }

    [Fact]
    public void AllOf_keeps_what_any_branch_knows()
    {
        Assert.Equal("""{"a":1,"b":"y"}""", Z.AllOf(A, B).Parse("""{"a":1,"b":"y","x":2}""").GetRawText());
    }

    [Fact]
    public void AllOf_merges_objects_inside_objects_and_arrays()
    {
        JsonValueSchema schema = Z.AllOf(
            Z.Object().Property("in", A).Property("list", Z.Array(A)),
            Z.Object().Property("in", B).Property("list", Z.Array(B)));

        Assert.Equal(
            """{"in":{"a":1,"b":"y"},"list":[{"a":1,"b":"y"}]}""",
            schema.Parse("""{"in":{"a":1,"b":"y","x":2},"list":[{"a":1,"b":"y","x":2}]}""").GetRawText());
    }

    [Fact]
    public void AllOf_keeps_a_changed_value_and_a_default_of_any_branch()
    {
        JsonValueSchema schema = Z.AllOf(
            Z.Object().Property("t", Z.String().Trim()),
            Z.Object().Property("d", Z.Integer().Default(5)).Property("t", Z.String()));

        Assert.Equal("""{"t":"a","d":5}""", schema.Parse("""{"t":" a "}""").GetRawText());
    }

    [Fact]
    public void A_property_no_branch_knows_is_gone()
    {
        Assert.False(Z.AllOf(A, B).Parse("""{"a":1,"b":"y","x":2}""").TryGetProperty("x", out _));
    }

    [Fact]
    public void AllOf_with_strict_branches_still_rejects_each_others_properties()
    {
        Assert.False(Z.AllOf(A.Strict(), B.Strict()).SafeParse("""{"a":1,"b":"y"}""").IsSuccess);
    }

    [Theory]
    [InlineData("""{"a":1,"x":2}""")]
    public void A_branch_that_changes_nothing_keeps_what_it_saw(string json)
    {
        Assert.Equal(json, Z.AllOf(A, Z.Object().Passthrough()).Parse(json).GetRawText());
        Assert.Equal(json, Z.AllOf(Z.Object().Passthrough(), A).Parse(json).GetRawText());
        Assert.Equal(json, Z.AllOf(A, Z.Any()).Parse(json).GetRawText());
    }

    [Fact]
    public void A_big_value_that_every_branch_keeps_is_not_rebuilt()
    {
        ObjectSchema inner = Z.Object().Property("p", Z.Any());
        string json = """{"p":[""" + string.Join(',', System.Linq.Enumerable.Repeat("1", 2_000_000)) + """],"x":1}""";
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

        Assert.True(Z.AllOf(inner, Z.Any()).SafeParse(json).IsSuccess);
        Assert.True(clock.Elapsed < System.TimeSpan.FromSeconds(20), clock.Elapsed.ToString());
    }

    [Fact]
    public void A_long_array_under_allOf_stays_cheap()
    {
        string json = "[" + string.Join(',', System.Linq.Enumerable.Repeat("""{"a":1,"b":"y","x":2}""", 100_000)) + "]";
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

        ParseResult<System.Text.Json.JsonElement> result = Z.AllOf(Z.Array(A), Z.Array(B)).SafeParse(json);

        Assert.True(clock.Elapsed < System.TimeSpan.FromSeconds(30), clock.Elapsed.ToString());
        Assert.True(result.IsSuccess || result.Issues.Length > 0);
    }

    [Fact]
    public void The_output_schema_does_not_close_what_parsing_leaves_open()
    {
        SchemaOptionsCheck(Z.If(Z.Object().Property("k", Z.String().Const("x")), Z.Object().Passthrough().Property("b", Z.Integer()), Z.Never()));
        SchemaOptionsCheck(Z.Array().Contains(Z.Object().Property("a", Z.String())));
        SchemaOptionsCheck(Z.AllOf(A, B));

        static void SchemaOptionsCheck<T>(Schema<T> schema) =>
            Assert.DoesNotContain("additionalProperties", schema.ToJsonSchema(new() { Direction = SchemaDirection.Output }).ToJsonString(), System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_a_rule_asks_for_stays()
    {
        ObjectSchema schema = Z.Object().Property("a", Z.Integer().Optional()).DependentRequired("a", "b");

        Assert.Equal("""{"a":1,"b":2}""", schema.Parse("""{"a":1,"b":2}""").GetRawText());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Items_that_differ_only_in_what_is_stripped_are_equal_to_uniqueItems(bool negated)
    {
        ArraySchema<System.Text.Json.JsonElement> unique = Z.Array(Z.Object().Property("a", Z.String())).UniqueItems();
        const string Json = """[{"a":"1","x":1},{"a":"1","x":2}]""";

        Assert.False(unique.SafeParse(Json).IsSuccess);

        if (negated)
        {
            Assert.DoesNotContain("uniqueItems", Z.Not(unique).ToJsonSchema().ToJsonString(), System.StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_deep_value_under_allOf_does_not_crash()
    {
        const int Depth = 2_000;
        JsonValueSchema tree = Z.Lazy("t", () => Tree!);
        string json = string.Concat(System.Linq.Enumerable.Repeat("""{"c":""", Depth)) + "{}" + new string('}', Depth);

        Assert.Null(Record.Exception(() => Z.AllOf(tree, tree).SafeParse(json)));
    }

    private static readonly ObjectSchema Tree = Z.Object().Property("c", Z.Lazy("t", () => Tree!).Optional());
}
