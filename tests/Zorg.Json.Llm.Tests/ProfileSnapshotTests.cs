using System.Linq;
using Xunit;
using Zorg.Json.Emission;

namespace Zorg.Json.Llm.Tests;

public sealed class ProfileSnapshotTests
{
    public static TheoryData<string> ProfileNames => ["openai", "anthropic", "gemini", "gemini-openapi"];

    [Fact]
    public void OpenAIStrict_order() => Snapshot.Match(Samples.Json(Samples.Order.Emit(LlmProfiles.OpenAIStrict).Schema));

    [Fact]
    public void AnthropicStrict_order() => Snapshot.Match(Samples.Json(Samples.Order.Emit(LlmProfiles.AnthropicStrict).Schema));

    [Fact]
    public void GeminiJsonSchema_order() => Snapshot.Match(Samples.Json(Samples.Order.Emit(LlmProfiles.GeminiJsonSchema).Schema));

    [Fact]
    public void GeminiOpenApi_order() => Snapshot.Match(Samples.Json(Samples.Order.Emit(LlmProfiles.GeminiOpenApi).Schema));

    [Fact]
    public void OpenAIStrict_diagnostics() => Snapshot.Match(Diagnostics(LlmProfiles.OpenAIStrict), "txt");

    [Fact]
    public void AnthropicStrict_diagnostics() => Snapshot.Match(Diagnostics(LlmProfiles.AnthropicStrict), "txt");

    [Fact]
    public void OpenAIStrict_wraps_array_root() =>
        Snapshot.Match(Samples.Json(Z.Array(Z.String()).MaxItems(3).Emit(LlmProfiles.OpenAIStrict).Schema));

    [Fact]
    public void GeminiJsonSchema_keeps_array_root() =>
        Snapshot.Match(Samples.Json(Z.Array(Z.String()).MaxItems(3).Emit(LlmProfiles.GeminiJsonSchema).Schema));

    [Fact]
    public void OpenAIStrict_discriminated_union() => Snapshot.Match(Samples.Json(Shape().Emit(LlmProfiles.OpenAIStrict).Schema));

    [Fact]
    public void GeminiJsonSchema_discriminated_union() => Snapshot.Match(Samples.Json(Shape().Emit(LlmProfiles.GeminiJsonSchema).Schema));

    [Theory]
    [MemberData(nameof(ProfileNames))]
    public void Emitted_schema_only_uses_keywords_the_table_allows(string name)
    {
        CompatibilityReport report = Samples.Order.CheckCompatibility(Profile(name));

        Assert.DoesNotContain(report.Issues, issue => issue.Kind == CompatibilityIssueKind.UnsupportedKeyword);
    }

    [Theory]
    [MemberData(nameof(ProfileNames))]
    public void Emitted_schema_has_no_schema_keyword(string name) =>
        Assert.False(Samples.Order.Emit(Profile(name)).Schema.ContainsKey("$schema"));

    [Fact]
    public void Profile_can_use_an_older_table()
    {
        SchemaCapabilities table = OpenAICapabilities.AsOf2026_11 with { Keywords = OpenAICapabilities.AsOf2026_11.Keywords.Remove("pattern") };

        string json = Z.Object().Property("code", Z.String().Pattern("^a")).Emit(LlmProfiles.CreateOpenAIStrict(table)).Schema.ToJsonString();

        Assert.DoesNotContain("\"pattern\"", json);
        Assert.Contains("pattern ^a", json);
    }

    private static JsonSchemaProfile Profile(string name) => name switch
    {
        "openai" => LlmProfiles.OpenAIStrict,
        "anthropic" => LlmProfiles.AnthropicStrict,
        "gemini" => LlmProfiles.GeminiJsonSchema,
        _ => LlmProfiles.GeminiOpenApi,
    };

    private static string Diagnostics(JsonSchemaProfile profile) =>
        string.Join('\n', Samples.Order.Emit(profile).Diagnostics.Select(diagnostic => $"{diagnostic.Keyword ?? "(refinement)"} @{diagnostic.Location}: {diagnostic.Message}"));

    private static JsonValueSchema Shape() => Z.DiscriminatedUnion(
        "kind",
        Z.Object().Property("kind", Z.String().Const("circle")).Property("radius", Z.Number().Optional()),
        Z.Object().Property("kind", Z.String().Const("square")).Property("side", Z.Number()));
}
