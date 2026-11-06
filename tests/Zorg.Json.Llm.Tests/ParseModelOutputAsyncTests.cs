using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Zorg.Json.Emission;

namespace Zorg.Json.Llm.Tests;

public sealed class ParseModelOutputAsyncTests
{
    private static readonly ObjectSchema Pair = Z.Object()
        .Property("a", Z.String().RefineAsync((text, _) => ValueTask.FromResult(text != "taken"), "taken"))
        .Property("k", Z.String().Optional());

    private static readonly EmitResult PairEmitted = Pair.Emit(LlmProfiles.OpenAIStrict);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Runs_async_refinements_that_the_sync_overload_refuses()
    {
        ParseResult<JsonElement> accepted = await Pair.ParseModelOutputAsync(PairEmitted, """{"a":"free","k":null}""", Token);
        ParseResult<JsonElement> refused = await Pair.ParseModelOutputAsync(PairEmitted, """{"a":"taken","k":null}""", Token);

        Assert.True(accepted.IsSuccess);
        Assert.False(accepted.Value.TryGetProperty("k", out _));
        Assert.False(refused.IsSuccess);
        Assert.Throws<InvalidOperationException>(() => Pair.ParseModelOutput(PairEmitted, """{"a":"free","k":null}"""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("not json")]
    [InlineData("""{"a":"x","a":"y"}""")]
    [InlineData("""{"a":"\ud83d","k":null}""")]
    public async Task Malformed_output_is_a_failed_result(string json)
    {
        ParseResult<JsonElement> result = await Pair.ParseModelOutputAsync(PairEmitted, json, Token);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task A_literal_lone_surrogate_is_a_failed_result()
    {
        string json = "{\"a\":\"" + (char)0xD83D + "\",\"k\":null}";

        ParseResult<JsonElement> result = await Pair.ParseModelOutputAsync(PairEmitted, json, Token);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task The_element_overload_adapts_the_same_way()
    {
        ParseResult<JsonElement> result = await Pair.ParseModelOutputAsync(PairEmitted, JsonElement.Parse("""{"a":"free","k":null}"""), Token);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.TryGetProperty("k", out _));
    }

    [Fact]
    public async Task Cancellation_reaches_the_refinement()
    {
        using CancellationTokenSource source = new();
        ObjectSchema schema = Z.Object().Property("a", Z.String().RefineAsync(async (_, token) =>
        {
            await source.CancelAsync();
            token.ThrowIfCancellationRequested();
            return true;
        }, "never"));
        EmitResult emitted = schema.Emit(LlmProfiles.OpenAIStrict);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await schema.ParseModelOutputAsync(emitted, """{"a":"x"}""", source.Token));
    }

    [Fact]
    public async Task Null_arguments_are_refused()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await Pair.ParseModelOutputAsync(PairEmitted, (string)null!, Token));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await Pair.ParseModelOutputAsync(null!, "{}", Token));
    }
}
