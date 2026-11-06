using System.Linq;
using System.Text.Json;
using Xunit;
using Zorg.Json.Emission;

namespace Zorg.Json.Llm.Tests;

public sealed class ModelOutputRobustnessTests
{
    private static readonly ObjectSchema Pair = Z.Object()
        .Property("a", Z.String())
        .Property("k", Z.String().Optional());

    private static readonly EmitResult PairEmitted = Pair.Emit(LlmProfiles.OpenAIStrict);

    [Theory]
    [InlineData("""{"a":"\ud83d","k":"x"}""")]
    [InlineData("""{"a":"x","k":"\ud83d"}""")]
    [InlineData("""{"a":"x","k":null,"\ud83d":1}""")]
    [InlineData("""{"a":"\ud83d","k":null}""")]
    public void Lone_surrogate_escape_does_not_throw(string json)
    {
        ParseResult<JsonElement> result = Pair.ParseModelOutput(PairEmitted, json);

        Assert.Equal(result.IsSuccess, result.Issues.Length == 0);
    }

    [Fact]
    public void Truncated_emoji_escape_is_reported_for_its_own_property_only()
    {
        ParseResult<JsonElement> result = Pair.ParseModelOutput(PairEmitted, """{"a":"\ud83d","k":null}""");

        Assert.False(result.IsSuccess);
        Assert.All(result.Issues, issue => Assert.Equal("/a", issue.InstanceLocation));
    }

    [Fact]
    public void Literal_lone_surrogate_does_not_throw()
    {
        string json = "{\"a\":\"" + (char)0xD83D + "\",\"k\":null}";

        ParseResult<JsonElement> result = Pair.ParseModelOutput(PairEmitted, json);

        Assert.Equal(result.IsSuccess, result.Issues.Length == 0);
    }

    [Fact]
    public void Invalid_utf8_does_not_throw()
    {
        byte[] bytes = [.. "{\"a\":\""u8, 0xC3, 0x28, .. "\",\"k\":null}"u8];

        ParseResult<JsonElement> result = Pair.ParseModelOutput(PairEmitted, bytes);

        Assert.Equal(result.IsSuccess, result.Issues.Length == 0);
    }

    [Fact]
    public void Element_with_duplicate_keys_does_not_throw()
    {
        using JsonDocument document = JsonDocument.Parse("""{"a":"x","a":"y","k":null}""", new JsonDocumentOptions { AllowDuplicateProperties = true });

        ParseResult<JsonElement> result = Pair.ParseModelOutput(PairEmitted, document.RootElement);

        Assert.Equal(result.IsSuccess, result.Issues.Length == 0);
    }

    [Fact]
    public void Element_deeper_than_64_does_not_throw()
    {
        ObjectSchema schema = Z.Object().Property("a", Z.Any()).Property("k", Z.String().Optional());
        string nested = string.Concat(Enumerable.Repeat("[", 100)) + string.Concat(Enumerable.Repeat("]", 100));
        using JsonDocument document = JsonDocument.Parse($$"""{"a":{{nested}},"k":null}""", new JsonDocumentOptions { MaxDepth = 200 });

        ParseResult<JsonElement> result = schema.ParseModelOutput(schema.Emit(LlmProfiles.OpenAIStrict), document.RootElement);

        Assert.Equal(result.IsSuccess, result.Issues.Length == 0);
    }

    [Fact]
    public void Huge_exponent_against_a_const_does_not_throw()
    {
        JsonValueSchema schema = Z.AnyOf(
            Z.Object().Property("k", Z.Integer().Const(1)),
            Z.Object().Property("k", Z.String()));

        ParseResult<JsonElement> result = schema.ParseModelOutput(schema.Emit(LlmProfiles.OpenAIStrict), """{"data":{"k":1e999999999999}}""");

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Self_referencing_union_does_not_overflow_the_stack()
    {
        JsonValueSchema? self = null;
        self = Z.AnyOf(Z.Lazy("t", () => self!), Z.Array(Z.String()));

        ParseResult<JsonElement> result = self.ParseModelOutput(self.Emit(LlmProfiles.OpenAIStrict), """{"data":["a"]}""");

        Assert.Equal(result.IsSuccess, result.Issues.Length == 0);
    }

    [Fact]
    public void Null_inside_a_record_value_is_undone_with_a_custom_profile()
    {
        JsonSchemaProfile profile = JsonSchemaProfile.Draft202012.With(Rewriters.RequireAllAsNullable);
        ObjectSchema schema = Z.Object().Property("m", Z.Record(Z.Object().Property("x", Z.String().Optional())));
        EmitResult emitted = schema.Emit(new JsonSchemaOptions { Target = profile });

        ParseResult<JsonElement> result = schema.ParseModelOutput(emitted, """{"m":{"k":{"x":null}}}""");

        Assert.True(result.IsSuccess, string.Join(", ", result.Issues.Select(issue => issue.Message)));
    }

    [Fact]
    public async System.Threading.Tasks.Task An_undefined_element_is_a_failed_result_not_an_exception()
    {
        ParseResult<JsonElement> result = Pair.ParseModelOutput(PairEmitted, default(JsonElement));
        ParseResult<JsonElement> async = await Pair.ParseModelOutputAsync(PairEmitted, default(JsonElement), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.False(async.IsSuccess);
    }
}
