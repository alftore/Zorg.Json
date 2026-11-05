using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Zorg.Json.Emission;

namespace Zorg.Json.Tests;

public sealed class Configured
{
    [DefaultValue(8080)]
    public int Port { get; set; }

    [DefaultValue("localhost")]
    public string? Host { get; set; }

    [DefaultValue(Color.DarkBlue)]
    public Color Theme { get; set; }

    [DefaultValue(5)]
    public long Retries { get; set; }

    [DefaultValue(3)]
    public int? Limit { get; set; }

    [DefaultValue(1)]
    public required int Mandatory { get; init; }

    public string? Plain { get; set; }
}

/// <summary>Pins the output of each dialect for the shapes that differ most between them.</summary>
public sealed class DialectOutputTests
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static readonly JsonValueSchema Conditional = Z.If(Z.String(), Z.String().MinLength(1), Z.Boolean());

    private static readonly JsonValueSchema Union = Z.DiscriminatedUnion(
        "kind",
        Z.Object().Property("kind", Z.String().Const("circle")).Property("radius", Z.Number().Nullable()),
        Z.Object().Property("kind", Z.String().Const("square")).Property("side", Z.Number()));

    private static readonly ObjectSchema Everything = Z.Object()
        .Property("conditional", Conditional)
        .Property("union", Union)
        .Property("nullable", Z.String().Nullable())
        .Property("tree", Tree.Schema);

    public static TheoryData<string, JsonSchemaProfile> Targets { get; } = new()
    {
        { "draft202012", JsonSchemaProfile.Draft202012 },
        { "draft07", JsonSchemaProfile.Draft07 },
        { "openapi30", JsonSchemaProfile.OpenApi30 },
        { "openapi31", JsonSchemaProfile.OpenApi31 },
    };

    [Theory]
    [MemberData(nameof(Targets))]
    public void Output_is_pinned(string name, JsonSchemaProfile target)
    {
        EmitResult result = Everything.Emit(new() { Target = target, Unrepresentable = UnrepresentablePolicy.Omit });

        Snapshot.Match(result.Schema.ToJsonString(Indented), testName: $"{nameof(Output_is_pinned)}_{name}");
    }

    [Fact]
    public void OpenApi30_reports_what_it_leaves_out()
    {
        EmitResult result = Conditional.Emit(new() { Target = JsonSchemaProfile.OpenApi30 });

        EmitDiagnostic diagnostic = Assert.Single(result.Diagnostics, item => item.Keyword == "if");
        Assert.False(result.Schema.ContainsKey("if"));
        Assert.Contains("openapi-3.0", diagnostic.Message, System.StringComparison.Ordinal);
        Assert.Contains("if", result.Schema["description"]!.GetValue<string>(), System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("draft202012")]
    [InlineData("draft07")]
    [InlineData("openapi31")]
    public void Other_dialects_keep_conditionals_and_report_nothing(string name)
    {
        JsonSchemaProfile target = name switch
        {
            "draft07" => JsonSchemaProfile.Draft07,
            "openapi31" => JsonSchemaProfile.OpenApi31,
            _ => JsonSchemaProfile.Draft202012,
        };

        EmitResult result = Conditional.Emit(new() { Target = target });

        Assert.True(result.Schema.ContainsKey("if"));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Definitions_keyword_follows_the_dialect()
    {
        Assert.True(Tree.Schema.ToJsonSchema().ContainsKey("$defs"));
        Assert.True(Tree.Schema.ToJsonSchema(new() { Target = JsonSchemaProfile.Draft07 }).ContainsKey("definitions"));
    }

    [Fact]
    public void Imported_additionalProperties_false_reports_unrecognized_keys()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""{ "properties": { "a": {} }, "additionalProperties": false }""");

        ParseIssue issue = Assert.Single(schema.SafeParse("""{ "a": 1, "b": 2, "c": 3 }""").Issues);

        Assert.Equal(IssueCodes.UnrecognizedKeys, issue.Code);
        Assert.Equal("additionalProperties", issue.Keyword);
        Assert.Equal(string.Empty, issue.InstanceLocation);
        Assert.Equal(["b", "c"], (string[])issue.Parameters["keys"]!);
    }

    [Fact]
    public void Imported_unevaluatedProperties_false_reports_unrecognized_keys()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""{ "unevaluatedProperties": false }""");

        ParseIssue issue = Assert.Single(schema.SafeParse("""{ "b": 2 }""").Issues);

        Assert.Equal(IssueCodes.UnrecognizedKeys, issue.Code);
        Assert.Equal("unevaluatedProperties", issue.Keyword);
    }

    [Fact]
    public void Imported_draft04_integers_have_no_openapi_format()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""{ "$schema": "http://json-schema.org/draft-04/schema#", "type": "integer" }""");

        Assert.False(schema.ToJsonSchema(new() { Target = JsonSchemaProfile.OpenApi31 }).ContainsKey("format"));
    }

    [Fact]
    public void Default_value_attribute_becomes_the_default_keyword()
    {
        JsonObject properties = Schema.Object<Configured>(WebWithStringEnums()).ToJsonSchema()["properties"]!.AsObject();

        Assert.Equal(8080, properties["port"]!["default"]!.GetValue<int>());
        Assert.Equal("localhost", properties["host"]!["default"]!.GetValue<string>());
        Assert.Equal("DarkBlue", properties["theme"]!["default"]!.GetValue<string>());
        Assert.Equal(5, properties["retries"]!["default"]!.GetValue<int>());
        Assert.Equal(3, properties["limit"]!["default"]!.GetValue<int>());
        Assert.False(properties["plain"]!.AsObject().ContainsKey("default"));
    }

    [Fact]
    public void Default_value_attribute_is_only_an_annotation()
    {
        Configured parsed = Schema.Object<Configured>(WebWithStringEnums()).Parse("""{ "mandatory": 1 }""");

        Assert.Equal(0, parsed.Port);
        Assert.Null(parsed.Host);
    }

    [Fact]
    public void Default_value_attribute_does_not_relax_required_members()
    {
        ObjectSchema<Configured> schema = Schema.Object<Configured>(WebWithStringEnums());

        Assert.Equal(["mandatory"], schema.ToNode().Required.ToArray());
        Assert.Equal(IssueCodes.Required, schema.SafeParse("{}").Issues[0].Code);
    }

    private static JsonSerializerOptions WebWithStringEnums() =>
        new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter<Color>() } };

    private static class Tree
    {
        public static readonly ObjectSchema Schema = Z.Object()
            .Property("value", Z.Integer())
            .Property("children", Z.Array(Z.Lazy("tree", () => Schema!)));
    }
}
