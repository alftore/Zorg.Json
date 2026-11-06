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
    public void The_input_schema_leaves_the_keyword_out_and_the_output_schema_closes()
    {
        Assert.Null(A.ToJsonSchema()["additionalProperties"]);
        Assert.False(A.ToJsonSchema(new() { Direction = SchemaDirection.Output })["additionalProperties"]!.GetValue<bool>());
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

    [Fact]
    public void The_output_schema_of_an_allOf_does_not_contradict_itself()
    {
        string text = Z.AllOf(A, B).ToJsonSchema(new() { Direction = SchemaDirection.Output }).ToJsonString();

        Assert.DoesNotContain("additionalProperties", text, System.StringComparison.Ordinal);
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
