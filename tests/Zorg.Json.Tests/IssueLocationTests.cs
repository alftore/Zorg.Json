using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Zorg.Json.Tests;

public sealed class IssueLocationTests
{
    private static ParseIssue Single<T>(Schema<T> schema, string json)
    {
        ParseResult<T> result = schema.SafeParse(json);

        Assert.False(result.IsSuccess);
        return Assert.Single(result.Issues);
    }

    private static JsonValueSchema Import(string schema) => Z.FromJsonSchema(JsonElement.Parse(schema));

    [Theory]
    [InlineData("""{ "properties": { "a": { "minLength": 3 } } }""", """{ "a": "x" }""", "/properties/a/minLength", "#/properties/a/minLength")]
    [InlineData("""{ "patternProperties": { "^a": { "minLength": 3 } } }""", """{ "ab": "x" }""", "/patternProperties/^a/minLength", "#/patternProperties/%5Ea/minLength")]
    [InlineData("""{ "additionalProperties": { "minLength": 3 } }""", """{ "ab": "x" }""", "/additionalProperties/minLength", "#/additionalProperties/minLength")]
    [InlineData("""{ "items": { "minLength": 3 } }""", """["abc", "x"]""", "/items/minLength", "#/items/minLength")]
    [InlineData("""{ "prefixItems": [{ "type": "string" }, { "minLength": 3 }] }""", """["abc", "x"]""", "/prefixItems/1/minLength", "#/prefixItems/1/minLength")]
    [InlineData("""{ "allOf": [{ "type": "string" }, { "minLength": 3 }] }""", "\"x\"", "/allOf/1/minLength", "#/allOf/1/minLength")]
    [InlineData("""{ "if": { "type": "string" }, "then": { "minLength": 3 } }""", "\"x\"", "/then/minLength", "#/then/minLength")]
    [InlineData("""{ "if": { "type": "string" }, "else": { "minimum": 3 } }""", "1", "/else/minimum", "#/else/minimum")]
    [InlineData("""{ "dependentSchemas": { "a": { "required": ["b"] } } }""", """{ "a": 1 }""", "/dependentSchemas/a/required", "#/dependentSchemas/a/required")]
    [InlineData("""{ "properties": { "a/b": { "minLength": 3 } } }""", """{ "a/b": "x" }""", "/properties/a~1b/minLength", "#/properties/a~1b/minLength")]
    public void Keywords_are_walked_in_both_paths(string schema, string json, string path, string location)
    {
        ParseIssue issue = Single(Import(schema), json);

        Assert.Equal(path, issue.EvaluationPath);
        Assert.Equal(location, issue.SchemaLocation);
    }

    [Fact]
    public void Root_keyword_has_a_short_path()
    {
        ParseIssue issue = Single(Z.String().MinLength(3), "\"a\"");

        Assert.Equal("/minLength", issue.EvaluationPath);
        Assert.Equal("#/minLength", issue.SchemaLocation);
    }

    [Fact]
    public void Contains_failure_is_reported_at_contains()
    {
        ParseIssue issue = Single(Import("""{ "contains": { "minimum": 5 } }"""), "[1]");

        Assert.Equal("/contains", issue.EvaluationPath);
    }

    [Fact]
    public void Not_failure_is_reported_at_not()
    {
        ParseIssue issue = Single(Import("""{ "not": { "type": "string" } }"""), "\"a\"");

        Assert.Equal("/not", issue.EvaluationPath);
        Assert.Equal("#/not", issue.SchemaLocation);
    }

    [Fact]
    public void False_schema_is_the_path_itself()
    {
        ParseIssue issue = Single(Import("""{ "properties": { "a": false } }"""), """{ "a": 1 }""");

        Assert.Equal("/properties/a", issue.EvaluationPath);
        Assert.Equal("#/properties/a", issue.SchemaLocation);
        Assert.Equal("false", issue.Keyword);
    }

    [Fact]
    public void Ref_is_walked_in_the_evaluation_path_and_resolved_in_the_schema_location()
    {
        JsonValueSchema schema = Import("""
            { "$defs": { "a": { "minLength": 3 } }, "properties": { "x": { "$ref": "#/$defs/a" }, "y": { "$ref": "#/$defs/a" } } }
            """);

        ParseIssue issue = Single(schema, """{ "x": "a" }""");

        Assert.Equal("/properties/x/$ref/minLength", issue.EvaluationPath);
        Assert.Equal("#/$defs/a/minLength", issue.SchemaLocation);
        Assert.Equal("/x", issue.InstanceLocation);
    }

    [Fact]
    public void Ref_through_another_ref_resolves_to_the_last_target()
    {
        JsonValueSchema schema = Import("""
            { "$defs": { "a": { "$ref": "#/$defs/b" }, "b": { "properties": { "n": { "maximum": 1 } } } }, "$ref": "#/$defs/a" }
            """);

        ParseIssue issue = Single(schema, """{ "n": 2 }""");

        Assert.Equal("/$ref/$ref/properties/n/maximum", issue.EvaluationPath);
        Assert.Equal("#/$defs/b/properties/n/maximum", issue.SchemaLocation);
    }

    [Fact]
    public void Ref_to_the_root_resolves_to_the_fragment_alone()
    {
        JsonValueSchema schema = Import("""{ "properties": { "next": { "$ref": "#" }, "n": { "maximum": 1 } } }""");

        ParseIssue issue = Single(schema, """{ "next": { "n": 2 } }""");

        Assert.Equal("/properties/next/$ref/properties/n/maximum", issue.EvaluationPath);
        Assert.Equal("#/properties/n/maximum", issue.SchemaLocation);
    }

    [Fact]
    public void Ref_with_a_name_that_needs_escaping_is_a_valid_fragment()
    {
        JsonValueSchema schema = Import("""
            { "$defs": { "a b/c": { "minLength": 3 } }, "$ref": "#/$defs/a%20b~1c" }
            """);

        ParseIssue issue = Single(schema, "\"x\"");

        Assert.Equal("/$ref/minLength", issue.EvaluationPath);
        Assert.Equal("#/$defs/a%20b~1c/minLength", issue.SchemaLocation);
    }

    [Fact]
    public void Siblings_of_ref_stay_beside_it()
    {
        JsonValueSchema schema = Import("""
            { "$schema": "https://json-schema.org/draft/2020-12/schema", "$defs": { "a": { "minLength": 3 } }, "$ref": "#/$defs/a", "maxLength": 1 }
            """);

        ParseIssue[] issues = [.. schema.SafeParse("\"xx\"").Issues];

        Assert.Equal(["/maxLength", "/$ref/minLength"], issues.Select(issue => issue.EvaluationPath));
        Assert.Equal(["#/maxLength", "#/$defs/a/minLength"], issues.Select(issue => issue.SchemaLocation));
    }

    private static readonly ObjectSchema Tree = Z.Object()
        .Property("value", Z.Integer().Maximum(9))
        .Property("children", Z.Array(Z.Lazy("tree", () => Tree!)).Optional());

    [Fact]
    public void Lazy_is_walked_through_ref()
    {
        ParseIssue issue = Single(Tree, """{ "value": 1, "children": [{ "value": 10 }] }""");

        Assert.Equal("/children/0/value", issue.InstanceLocation);
        Assert.Equal("/properties/children/items/$ref/properties/value/maximum", issue.EvaluationPath);
        Assert.Equal("#/$defs/tree/properties/value/maximum", issue.SchemaLocation);
    }

    [Fact]
    public void Lazy_two_levels_deep_repeats_the_ref_in_the_evaluation_path_only()
    {
        ParseIssue issue = Single(Tree, """{ "value": 1, "children": [{ "value": 1, "children": [{ "value": 10 }] }] }""");

        Assert.Equal("/properties/children/items/$ref/properties/children/items/$ref/properties/value/maximum", issue.EvaluationPath);
        Assert.Equal("#/$defs/tree/properties/value/maximum", issue.SchemaLocation);
    }

    [Fact]
    public void Union_branches_carry_their_own_paths_in_nested_issues()
    {
        JsonValueSchema schema = Import("""{ "anyOf": [{ "minLength": 3 }, { "type": "integer" }] }""");

        ParseIssue union = Single(schema, "\"x\"");
        ImmutableArray<ImmutableArray<ParseIssue>> branches = (ImmutableArray<ImmutableArray<ParseIssue>>)union.Parameters["errors"]!;

        Assert.Equal("/anyOf", union.EvaluationPath);
        Assert.Equal("/anyOf/0/minLength", branches[0][0].EvaluationPath);
        Assert.Equal("#/anyOf/0/minLength", branches[0][0].SchemaLocation);
        Assert.Equal("/anyOf/1/type", branches[1][0].EvaluationPath);
    }

    [Fact]
    public void OneOf_branches_carry_their_own_paths_in_nested_issues()
    {
        JsonValueSchema schema = Import("""{ "oneOf": [{ "minLength": 3 }, { "minLength": 4 }] }""");

        ImmutableArray<ImmutableArray<ParseIssue>> branches =
            (ImmutableArray<ImmutableArray<ParseIssue>>)Single(schema, "\"x\"").Parameters["errors"]!;

        Assert.Equal("/oneOf/1/minLength", branches[1][0].EvaluationPath);
    }

    [Fact]
    public void Discriminated_branch_is_walked_by_its_index()
    {
        JsonValueSchema schema = Z.DiscriminatedUnion("kind",
            Z.Object().Property("kind", Z.String().Const("a")),
            Z.Object().Property("kind", Z.String().Const("b")).Property("n", Z.Integer().Maximum(1)));

        ParseIssue issue = Single(schema, """{ "kind": "b", "n": 2 }""");

        Assert.Equal("/oneOf/1/properties/n/maximum", issue.EvaluationPath);
        Assert.Equal("#/oneOf/1/properties/n/maximum", issue.SchemaLocation);
    }

    [Fact]
    public void Required_is_reported_at_the_required_keyword()
    {
        ParseIssue issue = Single(Z.Object().Property("a", Z.String()), "{}");

        Assert.Equal("/required", issue.EvaluationPath);
        Assert.Equal("/a", issue.InstanceLocation);
    }

    [Fact]
    public void Refinement_issue_is_reported_at_the_refined_schema()
    {
        ObjectSchema refined = Z.Object().Property("a", Z.String().Refine(text => text.Length > 2, "short"));

        ParseIssue issue = Single(refined, """{ "a": "x" }""");

        Assert.Equal("/properties/a", issue.EvaluationPath);
        Assert.Equal("#/properties/a", issue.SchemaLocation);
    }

    [Fact]
    public void Invalid_json_has_no_schema_location()
    {
        ParseIssue issue = Assert.Single(Z.String().SafeParse("{").Issues);

        Assert.Equal(string.Empty, issue.EvaluationPath);
        Assert.Equal(string.Empty, issue.SchemaLocation);
    }

    [Fact]
    public void Instance_location_is_unchanged()
    {
        ParseIssue issue = Single(Import("""{ "properties": { "a/b": { "items": { "minLength": 3 } } } }"""), """{ "a/b": ["abc", "x"] }""");

        Assert.Equal("/a~1b/1", issue.InstanceLocation);
    }
}

public sealed class IssueTreeTests
{
    [Fact]
    public void Issues_at_the_root_are_errors_of_the_tree()
    {
        IssueTree tree = Z.String().MinLength(3).SafeParse("\"a\"").Issues.ToTree();

        Assert.Single(tree.Errors);
        Assert.Empty(tree.Children);
    }

    [Fact]
    public void Properties_are_children_by_name()
    {
        IssueTree tree = Z.Object().Property("name", Z.String().MinLength(3)).SafeParse("""{ "name": "a" }""").Issues.ToTree();

        Assert.Empty(tree.Errors);
        Assert.Single(tree.Children["name"].Errors);
    }

    [Fact]
    public void Array_items_are_children_by_index()
    {
        IssueTree tree = Z.Array(Z.String().MinLength(3)).SafeParse("""["abc", "a", "b"]""").Issues.ToTree();

        Assert.Equal(["1", "2"], tree.Children.Keys);
        Assert.Single(tree.Children["2"].Errors);
    }

    [Fact]
    public void Nested_levels_nest()
    {
        ObjectSchema schema = Z.Object().Property("list", Z.Array(Z.Object().Property("n", Z.Integer())));
        IssueTree tree = schema.SafeParse("""{ "list": [{ "n": "x" }] }""").Issues.ToTree();

        Assert.Single(tree.Children["list"].Children["0"].Children["n"].Errors);
    }

    [Fact]
    public void Several_issues_at_one_value_are_all_errors()
    {
        IssueTree tree = Z.String().MinLength(3).Pattern("^b").SafeParse("\"a\"").Issues.ToTree();

        Assert.Equal(2, tree.Errors.Length);
    }

    [Fact]
    public void Pointer_escapes_are_undone()
    {
        JsonValueSchema schema = Z.FromJsonSchema(JsonElement.Parse("""{ "properties": { "a/b": { "minLength": 3 }, "c~d": { "minLength": 3 } } }"""));
        IssueTree tree = schema.SafeParse("""{ "a/b": "x", "c~d": "x" }""").Issues.ToTree();

        Assert.Equal(["a/b", "c~d"], tree.Children.Keys.Order());
    }

    [Fact]
    public void Missing_property_is_a_child()
    {
        IssueTree tree = Z.Object().Property("a", Z.String()).SafeParse("{}").Issues.ToTree();

        Assert.Single(tree.Children["a"].Errors);
    }

    [Fact]
    public void No_issues_is_an_empty_tree()
    {
        IssueTree tree = ImmutableArray<ParseIssue>.Empty.ToTree();

        Assert.Empty(tree.Errors);
        Assert.Empty(tree.Children);
    }

    [Fact]
    public void Children_keep_the_order_of_the_issues()
    {
        List<string> keys = [.. Z.Object().Passthrough().Property("b", Z.String()).Property("a", Z.String()).SafeParse("""{ "b": 1, "a": 1 }""").Issues.ToTree().Children.Keys];

        Assert.Equal(["b", "a"], keys);
    }
}

public sealed class SizeBoundTests
{
    [Fact]
    public void Items_are_not_validated_past_maxItems()
    {
        int calls = 0;
        ArraySchema<string> schema = Z.Array(Z.String().Refine(_ => ++calls > 0, "never")).MaxItems(2);

        var result = schema.SafeParse("""["a","b","c","d","e"]""");

        Assert.False(result.IsSuccess);
        Assert.Equal("too_big", Assert.Single(result.Issues).Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Items_are_validated_up_to_maxItems()
    {
        int calls = 0;
        ArraySchema<string> schema = Z.Array(Z.String().Refine(_ => ++calls > 0, "never")).MaxItems(2);

        Assert.True(schema.SafeParse("""["a","b"]""").IsSuccess);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Item_issues_are_dropped_beside_too_big_but_the_verdict_stays_invalid()
    {
        var result = Z.Array(Z.Integer()).MaxItems(1).SafeParse("""["x","y"]""");

        Assert.False(result.IsSuccess);
        Assert.Equal("maxItems", Assert.Single(result.Issues).Keyword);
    }

    [Fact]
    public void Contains_and_uniqueItems_are_skipped_past_maxItems()
    {
        var result = Z.Array().Contains(Z.Integer()).UniqueItems().MaxItems(1).SafeParse("""["x","x"]""");

        Assert.Equal("maxItems", Assert.Single(result.Issues).Keyword);
    }

    [Fact]
    public void Applicators_beside_an_oversized_array_are_skipped()
    {
        int calls = 0;
        ArraySchema<JsonElement> schema = Z.Array().MaxItems(1).Refine(_ => ++calls > 0, "never");

        Assert.False(schema.SafeParse("[1,2]").IsSuccess);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Oversized_array_inside_not_still_counts_as_failing()
    {
        JsonValueSchema schema = Z.Not(Z.Array().MaxItems(1));

        Assert.True(schema.SafeParse("[1,2]").IsSuccess);
        Assert.False(schema.SafeParse("[1]").IsSuccess);
    }

    [Fact]
    public void Oversized_array_in_a_union_branch_falls_to_the_other_branch()
    {
        JsonValueSchema schema = Z.AnyOf(Z.Array().MaxItems(1), Z.Array().MinItems(2));

        Assert.True(schema.SafeParse("[1,2]").IsSuccess);
    }

    [Fact]
    public void Properties_are_not_validated_past_maxProperties()
    {
        int calls = 0;
        ObjectSchema schema = Z.Object().Passthrough().AdditionalProperties(Z.String().Refine(_ => ++calls > 0, "never")).MaxProperties(1);

        ParseResult<JsonElement> result = schema.SafeParse("""{ "a": "1", "b": "2", "c": "3" }""");

        Assert.False(result.IsSuccess);
        Assert.Equal("maxProperties", Assert.Single(result.Issues).Keyword);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Properties_up_to_maxProperties_are_validated()
    {
        int calls = 0;
        ObjectSchema schema = Z.Object().Passthrough().AdditionalProperties(Z.String().Refine(_ => ++calls > 0, "never")).MaxProperties(2);

        Assert.True(schema.SafeParse("""{ "a": "1", "b": "2" }""").IsSuccess);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Strip_keeps_walking_because_the_final_count_can_drop_below_the_limit()
    {
        ObjectSchema schema = Z.Object().Strip().Property("a", Z.String()).MaxProperties(1);

        Assert.True(schema.SafeParse("""{ "a": "1", "b": 2, "c": 3 }""").IsSuccess);
    }

    [Fact]
    public void Strip_still_fails_when_the_stripped_object_is_too_big()
    {
        ObjectSchema schema = Z.Object().Strip().Property("a", Z.String()).Property("b", Z.String()).MaxProperties(1);

        Assert.False(schema.SafeParse("""{ "a": "1", "b": "2", "c": 3 }""").IsSuccess);
    }

    [Fact]
    public void Default_counts_toward_maxProperties_without_the_early_stop_changing_the_verdict()
    {
        ObjectSchema schema = Z.Object().Property("a", Z.String()).Property("b", Z.String().Default("x")).MaxProperties(1);

        Assert.False(schema.SafeParse("""{ "a": "1" }""").IsSuccess);
    }

    [Fact]
    public void Million_items_over_the_bound_finish_without_walking()
    {
        string json = "[" + string.Join(',', Enumerable.Repeat("1", 1_000_000)) + "]";
        int calls = 0;
        ArraySchema<long> schema = Z.Array(Z.Integer().Refine(_ => ++calls > 0, "never")).MaxItems(10);

        Assert.False(schema.SafeParse(json).IsSuccess);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void UniqueItems_finds_the_first_duplicate_in_a_long_array()
    {
        string json = "[" + string.Join(',', Enumerable.Range(0, 50_000)) + ",7]";

        ParseIssue issue = Assert.Single(Z.Array().UniqueItems().SafeParse(json).Issues);

        Assert.Equal(new[] { 7, 50_000 }, (int[])issue.Parameters["indexes"]!);
    }

    [Theory]
    [InlineData("[1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 1.0]", 0, 22)]
    [InlineData("[\"a\", \"b\", \"c\", \"d\", \"e\", \"f\", \"g\", \"h\", \"i\", \"j\", \"k\", \"l\", \"m\", \"n\", \"o\", \"p\", \"q\", \"\\u0061\"]", 0, 17)]
    [InlineData("[{\"a\":1,\"b\":2}, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, {\"b\":2.0,\"a\":1}]", 0, 19)]
    [InlineData("[0, -0, 1]", 0, 1)]
    public void UniqueItems_agrees_with_json_equality(string json, int first, int second)
    {
        ParseIssue issue = Assert.Single(Z.Array().UniqueItems().SafeParse(json).Issues);

        Assert.Equal(new[] { first, second }, (int[])issue.Parameters["indexes"]!);
    }

    [Fact]
    public void UniqueItems_accepts_distinct_values_that_hash_alike()
    {
        string json = "[" + string.Join(',', Enumerable.Range(0, 30).Select(index => $"{{\"n\":{index}}}")) + "]";

        Assert.True(Z.Array().UniqueItems().SafeParse(json).IsSuccess);
    }
}
