using System.Linq;
using System.Text.Json;
using Xunit;
using Zorg.Json.Emission;

namespace Zorg.Json.Llm.Tests;

public sealed class ParseModelOutputTests
{
    private static readonly EmitResult OpenAIOrder = Samples.Order.Emit(LlmProfiles.OpenAIStrict);

    [Fact]
    public void Strict_model_output_parses_like_the_original()
    {
        ParseResult<JsonElement> fromModel = Samples.Order.ParseModelOutput(OpenAIOrder, Samples.ValidOrderFromStrictModel);
        ParseResult<JsonElement> direct = Samples.Order.SafeParse(Samples.ValidOrder);

        Assert.True(fromModel.IsSuccess, string.Join(", ", fromModel.Issues.Select(issue => issue.Message)));
        Assert.True(direct.IsSuccess);
        Assert.False(fromModel.Value.GetProperty("customer").TryGetProperty("email", out _));
        Assert.Equal(direct.Value.GetProperty("priority").GetInt64(), fromModel.Value.GetProperty("priority").GetInt64());
    }

    [Fact]
    public void Null_for_an_optional_property_fails_without_the_adapter() =>
        Assert.False(Samples.Order.SafeParse(Samples.ValidOrderFromStrictModel).IsSuccess);

    [Fact]
    public void Null_becomes_absent_so_the_default_applies()
    {
        ParseResult<JsonElement> result = Samples.Order.ParseModelOutput(OpenAIOrder, Samples.ValidOrderFromStrictModel);

        Assert.Equal(3, result.Value.GetProperty("priority").GetInt64());
    }

    [Fact]
    public void Optional_and_nullable_property_keeps_null()
    {
        ParseResult<JsonElement> result = Samples.Order.ParseModelOutput(OpenAIOrder, Samples.ValidOrderFromStrictModel);

        Assert.Equal(JsonValueKind.Null, result.Value.GetProperty("note").ValueKind);
    }

    [Fact]
    public void Required_nullable_property_keeps_null()
    {
        ParseResult<JsonElement> result = Samples.Order.ParseModelOutput(OpenAIOrder, Samples.ValidOrderFromStrictModel);

        Assert.Equal(JsonValueKind.Null, result.Value.GetProperty("customer").GetProperty("nickname").ValueKind);
    }

    [Fact]
    public void Null_for_a_required_property_still_fails()
    {
        string json = Samples.ValidOrderFromStrictModel.Replace("\"status\": \"open\"", "\"status\": null");

        ParseResult<JsonElement> result = Samples.Order.ParseModelOutput(OpenAIOrder, json);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Issues, issue => issue.InstanceLocation == "/status");
    }

    [Fact]
    public void Relocated_constraint_is_enforced()
    {
        string json = Samples.ValidOrderFromStrictModel.Replace("\"name\": \"Ada\"", "\"name\": \"\"");

        ParseResult<JsonElement> result = Samples.Order.ParseModelOutput(OpenAIOrder, json);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Issues, issue => issue.Code == IssueCodes.TooSmall && issue.InstanceLocation == "/customer/name");
    }

    [Fact]
    public void Refinement_is_enforced()
    {
        string json = Samples.ValidOrderFromStrictModel.Replace("\"Z123\"", "\"Z12\"");

        ParseResult<JsonElement> result = Samples.Order.ParseModelOutput(OpenAIOrder, json);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Issues, issue => issue.Code == IssueCodes.Custom);
    }

    [Fact]
    public void Null_inside_recursive_array_items_is_undone()
    {
        EmitResult emitted = Samples.Category.Emit(LlmProfiles.OpenAIStrict);
        const string Json = """{ "name": "a", "children": [{ "name": "b", "children": [{ "name": "c", "children": null }] }] }""";

        ParseResult<JsonElement> result = Samples.Category.ParseModelOutput(emitted, Json);

        Assert.True(result.IsSuccess, string.Join(", ", result.Issues.Select(issue => issue.Message)));
    }

    [Fact]
    public void Null_inside_the_matching_union_branch_is_undone()
    {
        JsonValueSchema shape = Z.DiscriminatedUnion(
            "kind",
            Z.Object().Property("kind", Z.String().Const("circle")).Property("radius", Z.Number().Optional()),
            Z.Object().Property("kind", Z.String().Const("square")).Property("side", Z.Number().Optional()));
        ObjectSchema schema = Z.Object().Property("shape", shape);

        ParseResult<JsonElement> result = schema.ParseModelOutput(schema.Emit(LlmProfiles.OpenAIStrict), """{ "shape": { "kind": "square", "side": null } }""");

        Assert.True(result.IsSuccess, string.Join(", ", result.Issues.Select(issue => issue.Message)));
        Assert.False(result.Value.GetProperty("shape").TryGetProperty("side", out _));
    }

    [Fact]
    public void Wrapped_root_is_unwrapped()
    {
        ArraySchema<string> schema = Z.Array(Z.String()).MaxItems(2);

        ParseResult<System.Collections.Generic.IReadOnlyList<string>> result =
            schema.ParseModelOutput(schema.Emit(LlmProfiles.OpenAIStrict), """{ "data": ["a", "b"] }""");

        Assert.True(result.IsSuccess);
        Assert.Equal(["a", "b"], result.Value);
    }

    [Fact]
    public void Issues_in_a_wrapped_root_use_the_original_paths()
    {
        ArraySchema<string> schema = Z.Array(Z.String().MinLength(2));

        ParseResult<System.Collections.Generic.IReadOnlyList<string>> result =
            schema.ParseModelOutput(schema.Emit(LlmProfiles.AnthropicStrict), """{ "data": ["ok", "x"] }""");

        Assert.False(result.IsSuccess);
        Assert.Equal("/1", result.Issues[0].InstanceLocation);
    }

    [Fact]
    public void Object_with_only_a_data_property_is_not_unwrapped()
    {
        ObjectSchema schema = Z.Object().Property("data", Z.String());

        ParseResult<JsonElement> result = schema.ParseModelOutput(schema.Emit(LlmProfiles.OpenAIStrict), """{ "data": "x" }""");

        Assert.True(result.IsSuccess);
        Assert.Equal("x", result.Value.GetProperty("data").GetString());
    }

    [Fact]
    public void Anthropic_keeps_optional_properties_so_null_stays_invalid()
    {
        ObjectSchema schema = Z.Object().Property("nick", Z.String().Optional());

        Assert.False(schema.ParseModelOutput(schema.Emit(LlmProfiles.AnthropicStrict), """{ "nick": null }""").IsSuccess);
        Assert.True(schema.ParseModelOutput(schema.Emit(LlmProfiles.AnthropicStrict), "{}").IsSuccess);
    }

    [Fact]
    public void Gemini_keeps_optional_properties_so_null_stays_invalid()
    {
        ObjectSchema schema = Z.Object().Property("nick", Z.String().Optional());

        Assert.False(schema.ParseModelOutput(schema.Emit(LlmProfiles.GeminiJsonSchema), """{ "nick": null }""").IsSuccess);
    }

    [Fact]
    public void Malformed_json_is_an_issue()
    {
        ParseResult<JsonElement> result = Samples.Order.ParseModelOutput(OpenAIOrder, "{");

        Assert.Equal(IssueCodes.InvalidJson, Assert.Single(result.Issues).Code);
    }

    [Fact]
    public void Malformed_utf8_json_is_an_issue()
    {
        ParseResult<JsonElement> result = Samples.Order.ParseModelOutput(OpenAIOrder, "{"u8);

        Assert.Equal(IssueCodes.InvalidJson, Assert.Single(result.Issues).Code);
    }

    [Fact]
    public void Duplicate_properties_are_an_issue()
    {
        ObjectSchema schema = Z.Object().Property("a", Z.String());

        ParseResult<JsonElement> result = schema.ParseModelOutput(schema.Emit(LlmProfiles.OpenAIStrict), """{ "a": "x", "a": "y" }""");

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Utf8_overload_undoes_rewrites()
    {
        ObjectSchema schema = Z.Object().Property("nick", Z.String().Optional());

        ParseResult<JsonElement> result = schema.ParseModelOutput(schema.Emit(LlmProfiles.OpenAIStrict), """{ "nick": null }"""u8);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Numbers_keep_their_text()
    {
        ObjectSchema schema = Z.Object().Property("n", Z.Number()).Property("o", Z.String().Optional());

        ParseResult<JsonElement> result = schema.ParseModelOutput(schema.Emit(LlmProfiles.OpenAIStrict), """{ "n": 1.50, "o": null }""");

        Assert.Equal("1.50", result.Value.GetProperty("n").GetRawText());
    }

    [Fact]
    public void Profile_without_rewrites_changes_nothing()
    {
        ObjectSchema schema = Z.Object().Property("nick", Z.String().Optional());

        Assert.False(schema.ParseModelOutput(schema.Emit(), """{ "nick": null }""").IsSuccess);
    }
}
