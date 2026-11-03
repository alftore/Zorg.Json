using System.Text.Json;
using Xunit;

namespace Zorg.Json.Tests;

public sealed class RegressionTests
{
    // The lookahead forces the backtracking engine, where this takes exponential time.
    private const string Catastrophic = "^(?=a)(a|aa)+b$";

    [Fact]
    public void Slow_property_pattern_is_an_issue_not_an_exception()
    {
        ObjectSchema schema = Z.Object().PatternProperty(Catastrophic, Z.Any());
        string json = $$"""{ "{{new string('a', 80)}}": 1 }""";

        ParseIssue issue = Assert.Single(schema.SafeParse(json).Issues);

        Assert.Equal(IssueCodes.InvalidFormat, issue.Code);
        Assert.Equal("patternProperties", issue.Keyword);
        Assert.Contains("too long", issue.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Slow_pattern_is_an_issue_not_an_exception()
    {
        StringSchema schema = Z.String().Pattern(Catastrophic);

        ParseIssue issue = Assert.Single(schema.SafeParse($"\"{new string('a', 80)}\"").Issues);

        Assert.Equal(IssueCodes.InvalidFormat, issue.Code);
        Assert.Contains("too long", issue.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Nullable_property_with_a_recursive_schema_allows_null()
    {
        ObjectSchema<Node> schema = NodeSchemas.Schema;

        Assert.Null(schema.Parse("""{ "id": "a", "next": null }""").Next);
        Assert.NotNull(schema.Parse("""{ "id": "a", "next": { "id": "b", "next": null } }""").Next);
        Assert.False(schema.SafeParse("""{ "id": "a", "next": 1 }""").IsSuccess);
        Assert.Contains("null", schema.ToJsonSchema().ToJsonString(), System.StringComparison.Ordinal);
    }

    [Fact]
    public void Nullable_wrapper_keeps_a_default()
    {
        ObjectSchema schema = Z.Object().Property("a", Z.Lazy("x", () => Z.String()).Default(JsonElement.Parse("\"d\"")));

        Assert.Equal("d", schema.Parse("{}").GetProperty("a").GetString());
    }

    private static class NodeSchemas
    {
        public static readonly ObjectSchema<Node> Schema = Zorg.Json.Schema.Object(TestJson.Default.Node)
            .Property(node => node.Next, Zorg.Json.Schema.Lazy<Node>("node", () => Schema!));
    }
}
