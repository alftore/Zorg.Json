using System;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Zorg.Json.Emission;

namespace Zorg.Json.Llm.Tests;

public sealed class ReferenceTests
{
    private const string RecursiveUnion = """{"anyOf":[{"type":"string"},{"type":"array","items":{"$ref":"#"}}]}""";

    [Fact]
    public void Wrapping_a_root_keeps_a_reference_to_the_root_pointing_at_it()
    {
        JsonValueSchema schema = Z.FromJsonSchema(RecursiveUnion);

        EmitResult emitted = schema.Emit(LlmProfiles.OpenAIStrict);

        Assert.Contains("\"$ref\":\"#/properties/data\"", emitted.Schema.ToJsonString(), StringComparison.Ordinal);
        Assert.True(schema.ParseModelOutput(emitted, """{"data":[["x"],"y"]}""").IsSuccess);
        Assert.False(schema.ParseModelOutput(emitted, """{"data":[{"data":"x"}]}""").IsSuccess);
    }

    [Fact]
    public void Wrapping_a_root_moves_references_into_the_original_by_pointer()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""{"anyOf":[{"type":"string"},{"type":"array","items":{"$ref":"#/anyOf/0"}}]}""");

        EmitResult emitted = schema.Emit(LlmProfiles.OpenAIStrict);

        Assert.Contains("\"$ref\":\"#/properties/data/anyOf/0\"", emitted.Schema.ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("openai-strict")]
    [InlineData("gemini-json-schema")]
    public void Root_definitions_become_defs_so_references_stay_valid(string name)
    {
        JsonValueSchema schema = Z.FromJsonSchema("""
            {"type":"object","properties":{"a":{"$ref":"#/definitions/x"}},"definitions":{"x":{"type":"string","minLength":2}}}
            """);

        EmitResult emitted = schema.Emit(Profile(name));
        string text = emitted.Schema.ToJsonString();

        Assert.Contains("\"$ref\":\"#/$defs/x\"", text, StringComparison.Ordinal);
        Assert.Contains("\"$defs\":{\"x\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("definitions", text, StringComparison.Ordinal);
        Assert.DoesNotContain(emitted.Diagnostics, diagnostic => diagnostic.Message.Contains("lead anywhere", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("openai-strict")]
    [InlineData("gemini-json-schema")]
    public void Anchor_references_become_pointers(string name)
    {
        JsonValueSchema schema = Z.FromJsonSchema("""
            {"type":"object","properties":{"a":{"$ref":"#x"}},"$defs":{"x":{"$anchor":"x","type":"string"}}}
            """);

        EmitResult emitted = schema.Emit(Profile(name));

        Assert.Contains("\"$ref\":\"#/$defs/x\"", emitted.Schema.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(emitted.Diagnostics, diagnostic => diagnostic.Message.Contains("lead anywhere", StringComparison.Ordinal));
    }

    [Fact]
    public void A_reference_that_cannot_be_kept_is_reported_as_an_error()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""
            {"type":"object","properties":{"a":{"$ref":"#/properties/b/definitions/x"},"b":{"type":"object","definitions":{"x":{"type":"string"}}}}}
            """);

        EmitResult emitted = schema.Emit(LlmProfiles.OpenAIStrict);
        CompatibilityReport report = schema.CheckCompatibility(LlmProfiles.OpenAIStrict);

        Assert.Contains(emitted.Diagnostics, diagnostic => diagnostic.Keyword == "$ref" && diagnostic.Message.Contains("lead anywhere", StringComparison.Ordinal));
        Assert.Contains(report.Issues, issue => issue.Kind == CompatibilityIssueKind.Reference && issue.Severity == CompatibilitySeverity.Error);
    }

    [Theory]
    [InlineData("openai-strict")]
    [InlineData("anthropic-strict")]
    [InlineData("gemini-json-schema")]
    [InlineData("gemini-openapi")]
    public void Malformed_keywords_inside_definitions_do_not_throw(string name)
    {
        JsonValueSchema schema = Z.FromJsonSchema("""
            {"definitions":{"a":{"minItems":"x","pattern":5,"type":7,"required":[1],"format":3,"description":4,"enum":{}}}}
            """);

        EmitResult emitted = schema.Emit(Profile(name));
        CompatibilityReport report = schema.CheckCompatibility(Profile(name));

        Assert.NotNull(emitted.Schema);
        Assert.NotNull(report);
    }

    [Theory]
    [InlineData("anthropic-strict")]
    [InlineData("gemini-openapi")]
    public async Task A_chain_of_doubling_definitions_is_capped(string name)
    {
        StringBuilder text = new("""{"$ref":"#/$defs/l0","$defs":{""");

        for (int level = 0; level < 26; level++)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"\"l{level}\":{{\"type\":\"object\",\"properties\":{{\"x\":{{\"$ref\":\"#/$defs/l{level + 1}\"}},\"y\":{{\"$ref\":\"#/$defs/l{level + 1}\"}}}}}},");
        }

        text.Append("""
            "l26":{"type":"string"}}}
            """);
        JsonValueSchema schema = Z.FromJsonSchema(text.ToString());

        EmitResult emitted = await Task.Run(() => schema.Emit(Profile(name)), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Contains(emitted.Diagnostics, diagnostic => diagnostic.Keyword == "$ref" && diagnostic.Message.Contains("too large", StringComparison.Ordinal));
        Assert.True(emitted.Schema.ToJsonString().Length < 5_000_000);
    }

    private static JsonSchemaProfile Profile(string name) => name switch
    {
        "openai-strict" => LlmProfiles.OpenAIStrict,
        "anthropic-strict" => LlmProfiles.AnthropicStrict,
        "gemini-json-schema" => LlmProfiles.GeminiJsonSchema,
        _ => LlmProfiles.GeminiOpenApi,
    };
}
