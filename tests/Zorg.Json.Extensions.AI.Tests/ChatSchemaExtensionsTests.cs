using System;
using System.Text;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Xunit;
using Zorg.Json.Emission;
using Zorg.Json.Llm;

namespace Zorg.Json.Extensions.AI.Tests;

public sealed class ChatSchemaExtensionsTests
{
    private static readonly ObjectSchema Answer = Z.Object()
        .Property("title", Z.String().MaxLength(80).Pattern("^[A-Z]"))
        .Property("score", Z.Integer().Minimum(0).Maximum(10))
        .Property("note", Z.String().Optional());

    private const string Good = """{"title":"Fine","score":7}""";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void The_format_carries_the_emitted_schema_name_and_description()
    {
        ChatResponseFormatJson format = Assert.IsType<ChatResponseFormatJson>(Answer.ToChatResponseFormat(schemaName: "answer", schemaDescription: "A rating."));

        Assert.Equal("answer", format.SchemaName);
        Assert.Equal("A rating.", format.SchemaDescription);
        Assert.NotNull(format.Schema);
        Assert.Equal(Answer.ToJsonSchema().ToJsonString(), format.Schema.Value.GetRawText());
    }

    [Fact]
    public void A_profile_shapes_the_schema_it_sends()
    {
        ChatResponseFormatJson format = Assert.IsType<ChatResponseFormatJson>(Answer.ToChatResponseFormat(LlmProfiles.OpenAIStrict));
        JsonElement schema = format.Schema!.Value;

        Assert.Equal(3, schema.GetProperty("required").GetArrayLength());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(JsonValueKind.Array, schema.GetProperty("properties").GetProperty("note").GetProperty("type").ValueKind);
    }

    [Fact]
    public void The_overload_with_an_out_parameter_returns_what_was_left_out()
    {
        ObjectSchema tree = Z.Object().Property("children", Z.Array(Z.Lazy("tree", () => Tree!)));
        ChatResponseFormat format = tree.ToChatResponseFormat(LlmProfiles.OpenAIStrict, out EmitResult emitted);

        Assert.NotNull(format);
        Assert.NotNull(emitted.Schema);
    }

    private static readonly ObjectSchema Tree = Z.Object().Property("children", Z.Array(Z.Lazy("tree", () => Tree!)));

    [Fact]
    public async Task A_valid_answer_is_a_success_with_the_value()
    {
        FakeChatClient client = new(Good);

        ParseResult<JsonElement> result = await client.GetResponseAsync(Answer, "Rate it.", cancellationToken: Token);

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Value.GetProperty("score").GetInt32());
    }

    [Fact]
    public async Task The_request_has_the_format_and_the_callers_options_are_left_alone()
    {
        FakeChatClient client = new(Good);
        ChatOptions options = new() { Temperature = 0.2f, ModelId = "m", ResponseFormat = ChatResponseFormat.Text };

        await client.GetResponseAsync(Answer, [new ChatMessage(ChatRole.User, "Rate it.")], options, schemaName: "answer", cancellationToken: Token);

        ChatOptions sent = Assert.Single(client.Options)!;
        Assert.NotSame(options, sent);
        Assert.Equal(0.2f, sent.Temperature);
        Assert.Equal("m", sent.ModelId);
        Assert.Equal("answer", Assert.IsType<ChatResponseFormatJson>(sent.ResponseFormat).SchemaName);
        Assert.Same(ChatResponseFormat.Text, options.ResponseFormat);
        Assert.Equal("Rate it.", Assert.Single(Assert.Single(client.Requests)).Text);
    }

    [Fact]
    public async Task The_string_overload_sends_one_user_message()
    {
        FakeChatClient client = new(Good);

        await client.GetResponseAsync(Answer, "Rate it.", cancellationToken: Token);

        ChatMessage message = Assert.Single(Assert.Single(client.Requests));
        Assert.Equal(ChatRole.User, message.Role);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Sorry, I can't help with that.")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("{")]
    [InlineData("""{"title":"Fine","score":""")]
    [InlineData("""{"title":"Fine","sco""")]
    [InlineData("""{"title":"Fine","score":7}}""")]
    [InlineData("""{"title":"Fine","score":7} trailing""")]
    [InlineData("```json\n{\"title\":\"Fine\",\"score\":7}\n```")]
    [InlineData("""{"title":"Fine","score":7,"score":8}""")]
    [InlineData("""{"title":"fine","score":7}""")]
    [InlineData("""{"title":"Fine","score":11}""")]
    [InlineData("""{"title":"Fine","score":"7"}""")]
    [InlineData("""{"title":"Fine","score":7,"extra":true}""")]
    [InlineData("""{"title":"Fine"}""")]
    [InlineData("""{"title":"Fine","score":7e999999999999}""")]
    [InlineData("""{"title":"\ud800","score":7}""")]
    [InlineData("""{"title":"Fine","score":7,"\ud800":1}""")]
    [InlineData("""{"title":"Fine","score":7,"note":"\udc00"}""")]
    public async Task Bad_output_is_a_failed_result_and_never_an_exception(string text)
    {
        ParseResult<JsonElement> result = await new FakeChatClient(text).GetResponseAsync(Answer, "Rate it.", cancellationToken: Token);

        Assert.False(result.IsSuccess);
        Assert.NotEmpty(result.Issues);
    }

    [Fact]
    public async Task Output_with_a_lone_surrogate_character_is_a_failed_result()
    {
        string text = "{\"title\":\"Fine" + (char)0xD83D + "\",\"score\":7}";

        ParseResult<JsonElement> result = await new FakeChatClient(text).GetResponseAsync(Answer, "Rate it.", cancellationToken: Token);

        Assert.False(result.IsSuccess);
        Assert.NotEmpty(result.Issues);
    }

    [Fact]
    public async Task Output_nested_far_deeper_than_the_limit_is_a_failed_result()
    {
        string text = new StringBuilder().Insert(0, "[", 100_000).Append(']', 100_000).ToString();

        ParseResult<JsonElement> result = await new FakeChatClient(text).GetResponseAsync(Answer, "Rate it.", cancellationToken: Token);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Output_that_stops_early_is_invalid_json()
    {
        ParseResult<JsonElement> result = await new FakeChatClient("""{"title":"Fine","sco""").GetResponseAsync(Answer, "Rate it.", cancellationToken: Token);

        Assert.Equal(IssueCodes.InvalidJson, Assert.Single(result.Issues).Code);
    }

    [Fact]
    public async Task A_response_with_no_messages_is_a_failed_result()
    {
        FakeChatClient client = new((_, _, _) => new ChatResponse());

        ParseResult<JsonElement> result = await client.GetResponseAsync(Answer, "Rate it.", cancellationToken: Token);

        Assert.Equal(IssueCodes.InvalidJson, Assert.Single(result.Issues).Code);
    }

    [Fact]
    public async Task A_response_with_only_a_tool_call_is_a_failed_result()
    {
        FakeChatClient client = new((_, _, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("1", "f")])));

        ParseResult<JsonElement> result = await client.GetResponseAsync(Answer, "Rate it.", cancellationToken: Token);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task A_client_that_returns_null_gives_a_failed_result()
    {
        FakeChatClient client = new((_, _, _) => null!);

        ParseResult<JsonElement> result = await client.GetResponseAsync(Answer, "Rate it.", cancellationToken: Token);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task A_truncated_answer_is_a_failed_result_whatever_the_finish_reason()
    {
        FakeChatClient client = new((_, _, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"title":"Fine","score":"""))
        {
            FinishReason = ChatFinishReason.Length,
        });

        ParseResult<JsonElement> result = await client.GetResponseAsync(Answer, "Rate it.", cancellationToken: Token);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task The_answer_is_the_last_message_with_text()
    {
        FakeChatClient client = new((_, _, _) => new ChatResponse(
        [
            new ChatMessage(ChatRole.Assistant, "Let me check."),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("1", "f")]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("1", "ok")]),
            new ChatMessage(ChatRole.Assistant, Good),
            new ChatMessage(ChatRole.Assistant, []),
        ]));

        ParseResult<JsonElement> result = await client.GetResponseAsync(Answer, "Rate it.", cancellationToken: Token);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task A_wrapped_root_is_unwrapped_when_the_profile_wrapped_it()
    {
        ArraySchema<string> list = Z.Array(Z.String()).MinItems(1);
        FakeChatClient client = new("""{"data":["a","b"]}""");

        ParseResult<IReadOnlyList<string>> result = await client.GetResponseAsync(list, "List them.", profile: LlmProfiles.OpenAIStrict, cancellationToken: Token);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Count);
        Assert.Equal("object", Assert.IsType<ChatResponseFormatJson>(client.Options[0]!.ResponseFormat).Schema!.Value.GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_wrapped_root_still_checks_the_full_schema()
    {
        ArraySchema<string> list = Z.Array(Z.String()).MinItems(1);

        ParseResult<IReadOnlyList<string>> result = await new FakeChatClient("""{"data":[]}""").GetResponseAsync(list, "List them.", profile: LlmProfiles.OpenAIStrict, cancellationToken: Token);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task A_null_for_an_optional_property_means_absent_under_a_strict_profile()
    {
        ParseResult<JsonElement> result = await new FakeChatClient("""{"title":"Fine","score":7,"note":null}""")
            .GetResponseAsync(Answer, "Rate it.", profile: LlmProfiles.OpenAIStrict, cancellationToken: Token);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.TryGetProperty("note", out _));
    }

    [Fact]
    public async Task Rules_the_provider_cannot_enforce_are_enforced_on_the_answer()
    {
        // The Gemini profile moves what it cannot send into the description, so only parsing can enforce it.
        ParseResult<JsonElement> result = await new FakeChatClient("""{"title":"lower","score":7}""")
            .GetResponseAsync(Answer, "Rate it.", profile: LlmProfiles.GeminiOpenApi, cancellationToken: Token);

        Assert.False(result.IsSuccess);
        Assert.Equal("/title", result.Issues[0].InstanceLocation);
    }

    [Fact]
    public async Task A_schema_the_profile_cannot_express_is_loosened_but_the_answer_is_still_checked_in_full()
    {
        ObjectSchema tree = Z.Object().Property("name", Z.String().MinLength(1)).Property("kids", Z.Array(Z.Lazy("node", () => Node!)).Optional());
        EmitResult emitted = tree.Emit(LlmProfiles.AnthropicStrict);
        FakeChatClient client = new("""{"name":"a","kids":[{"name":""}]}""");

        ParseResult<JsonElement> result = await client.GetResponseAsync(tree, "Build it.", profile: LlmProfiles.AnthropicStrict, cancellationToken: Token);

        Assert.NotEmpty(emitted.Diagnostics);
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Issues, issue => issue.InstanceLocation == "/kids/0/name");
    }

    private static readonly ObjectSchema Node = Z.Object().Property("name", Z.String().MinLength(1)).Property("kids", Z.Array(Z.Lazy("node", () => Node!)).Optional());

    [Fact]
    public async Task An_async_refinement_runs_on_the_answer()
    {
        ObjectSchema schema = Z.Object().Property("name", Z.String().RefineAsync((name, _) => ValueTask.FromResult(name != "taken"), "taken"));

        ParseResult<JsonElement> refused = await new FakeChatClient("""{"name":"taken"}""").GetResponseAsync(schema, "Name it.", cancellationToken: Token);
        ParseResult<JsonElement> accepted = await new FakeChatClient("""{"name":"free"}""").GetResponseAsync(schema, "Name it.", cancellationToken: Token);

        Assert.False(refused.IsSuccess);
        Assert.True(accepted.IsSuccess);
    }

    [Fact]
    public async Task A_client_exception_passes_through()
    {
        FakeChatClient client = new((_, _, _) => throw new InvalidOperationException("provider down"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await client.GetResponseAsync(Answer, "Rate it.", cancellationToken: Token));
    }

    [Fact]
    public async Task Cancellation_reaches_the_client_and_throws()
    {
        using CancellationTokenSource source = new();
        CancellationToken seen = default;
        FakeChatClient client = new((_, _, token) =>
        {
            seen = token;
            token.ThrowIfCancellationRequested();
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, Good));
        });
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await client.GetResponseAsync(Answer, "Rate it.", cancellationToken: source.Token));

        Assert.Equal(source.Token, seen);
    }

    [Fact]
    public async Task Cancelling_during_an_async_refinement_throws()
    {
        using CancellationTokenSource source = new();
        ObjectSchema schema = Z.Object().Property("name", Z.String().RefineAsync(async (_, token) =>
        {
            await source.CancelAsync();
            token.ThrowIfCancellationRequested();
            return true;
        }, "never"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new FakeChatClient("""{"name":"a"}""").GetResponseAsync(schema, "Name it.", cancellationToken: source.Token));
    }

    [Fact]
    public async Task A_mistake_in_the_call_itself_is_an_argument_exception()
    {
        FakeChatClient client = new(Good);

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await client.GetResponseAsync<JsonElement>(null!, "x", cancellationToken: Token));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await client.GetResponseAsync(Answer, (string)null!, cancellationToken: Token));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await ((IChatClient)null!).GetResponseAsync(Answer, "x", cancellationToken: Token));
        Assert.Throws<ArgumentNullException>(() => ((Schema<JsonElement>)null!).ToChatResponseFormat());
    }

    [Fact]
    public async Task Every_profile_gives_a_result_for_a_hostile_answer()
    {
        JsonSchemaProfile[] all = [LlmProfiles.OpenAIStrict, LlmProfiles.AnthropicStrict, LlmProfiles.GeminiJsonSchema, LlmProfiles.GeminiOpenApi];

        foreach (JsonSchemaProfile profile in all)
        {
            foreach (string text in new[] { "", "{", """{"data":null}""", """{"title":"\ud800","score":null,"note":null}""", "\uD800" })
            {
                ParseResult<JsonElement> result = await new FakeChatClient(text).GetResponseAsync(Answer, "Rate it.", profile: profile, cancellationToken: Token);

                Assert.False(result.IsSuccess, profile.Name);
            }
        }
    }

    [Fact]
    public void Tool_and_response_format_use_the_same_schema_text()
    {
        ZorgAIFunction function = ZorgAIFunction.Create("tool", "", Answer, _ => "x", LlmProfiles.AnthropicStrict);
        ChatResponseFormatJson format = Assert.IsType<ChatResponseFormatJson>(Answer.ToChatResponseFormat(LlmProfiles.AnthropicStrict));

        Assert.Equal(format.Schema!.Value.GetRawText(), function.JsonSchema.GetRawText());
    }
}
