using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Zorg.Json.Emission;

namespace Zorg.Json.Tests;

/// <summary>The examples in README.md and docs/guide.md, so that they can't drift from the API.</summary>
public sealed class DocumentationExamplesTests
{
    private static readonly ObjectSchema SearchSchema = Z.Object()
        .Property("query", Z.String().Trim().MinLength(1))
        .Property("limit", Z.Integer().Minimum(1).Maximum(50).Default(10))
        .Property("since", Z.String().Date().Optional());

    private static readonly ObjectSchema<Circle> CircleSchema = Schema.Object(TestJson.Default.Circle);

    private static readonly ObjectSchema<Square> SquareSchema = Schema.Object(TestJson.Default.Square);

    [Fact]
    public void Readme_example()
    {
        StringSchema name = Z.String().Trim().MinLength(1).MaxLength(100);

        ParseResult<string> result = name.SafeParse("\"  Ada  \"");
        JsonObject schema = name.ToJsonSchema();

        Assert.Equal("Ada", result.Value);
        Assert.Equal("string", schema["type"]!.GetValue<string>());
    }

    [Fact]
    public void Search_schema_fills_defaults_and_rejects_unknown_properties()
    {
        JsonElement parsed = SearchSchema.Parse("""{ "query": "  hi  " }""");

        Assert.Equal("hi", parsed.GetProperty("query").GetString());
        Assert.Equal(10, parsed.GetProperty("limit").GetInt32());
        Assert.Equal(IssueCodes.UnrecognizedKeys, Assert.Single(SearchSchema.SafeParse("""{ "query": "a", "x": 1 }""").Issues).Code);
    }

    [Fact]
    public void Map_converts_to_a_domain_object() =>
        Assert.Equal(2, Z.String().SafeParse("\"ab\"").Map(text => text.Length).Value);

    [Fact]
    public void Blank_input_fails_MinLength_after_Trim() =>
        Assert.False(Z.String().Trim().MinLength(1).SafeParse("\"   \"").IsSuccess);

    [Fact]
    public void Normalized_enum() =>
        Assert.Equal("web", Z.String().ToLowerInvariant().Enum("web", "news").Parse("\"WEB\""));

    [Fact]
    public void Union_examples()
    {
        Schema<Shape> shape = Schema.Union<Shape>("kind")
            .Case("circle", CircleSchema)
            .Case("square", SquareSchema);

        Assert.IsType<Square>(shape.Parse("""{ "kind": "square", "side": 1 }"""));
        Assert.True(Z.AnyOf(Z.String(), Z.Integer()).SafeParse("1").IsSuccess);
    }

    [Fact]
    public void Input_guarantee_example()
    {
        EmitResult result = Z.String().Trim().MaxLength(3).Emit();
        JsonObject emitted = result.Schema;
        ImmutableArray<EmitDiagnostic> leftOut = result.Diagnostics;

        Assert.Equal("After Trim: maxLength 3.", emitted["description"]!.GetValue<string>());
        Assert.False(emitted.ContainsKey("maxLength"));
        Assert.Equal("maxLength", Assert.Single(leftOut).Keyword);
    }

    [Fact]
    public void Issue_formatters()
    {
        Dictionary<string, string[]> errors = SearchSchema.SafeParse("""{ "limit": 0 }""").Issues.ToDictionary();

        Assert.Equal(2, errors.Count);
        Assert.True(errors.ContainsKey("/query"));
        Assert.True(errors.ContainsKey("/limit"));
    }
}
