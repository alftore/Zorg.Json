using System;
using System.Collections.Generic;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Json.Schema;
using Microsoft.Extensions.AI;
using OpenAI;
using Xunit;
using Zorg.Json.Llm;

namespace Zorg.Json.Extensions.AI.Tests;

/// <summary>
/// Sends our schemas through the real OpenAI adapter of Microsoft.Extensions.AI, to a handler that records the request and
/// answers from a script. No network and no key. This is the part of the provider path that can be tested without a provider.
/// </summary>
public sealed class OpenAIAdapterTests
{
    // Nothing the adapter moves: no pattern, minimum or length.
    private static readonly ObjectSchema Plain = Z.Object()
        .Property("title", Z.String())
        .Property("score", Z.Integer())
        .Property("note", Z.String().Optional());

    private static readonly ObjectSchema Ruled = Z.Object()
        .Property("title", Z.String().MaxLength(80).Pattern("^[A-Z]"))
        .Property("score", Z.Integer().Minimum(0).Maximum(10))
        .Property("note", Z.String().Optional());

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Completion(string content) => new JsonObject
    {
        ["id"] = "1",
        ["object"] = "chat.completion",
        ["created"] = 1,
        ["model"] = "gpt-4o",
        ["choices"] = new JsonArray(new JsonObject
        {
            ["index"] = 0,
            ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content },
            ["finish_reason"] = "stop",
        }),
    }.ToJsonString();

    private static string ToolCall(string name, string arguments) => new JsonObject
    {
        ["id"] = "2",
        ["object"] = "chat.completion",
        ["created"] = 1,
        ["model"] = "gpt-4o",
        ["choices"] = new JsonArray(new JsonObject
        {
            ["index"] = 0,
            ["message"] = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = null,
                ["tool_calls"] = new JsonArray(new JsonObject
                {
                    ["id"] = "call_1",
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments },
                }),
            },
            ["finish_reason"] = "tool_calls",
        }),
    }.ToJsonString();

    private static async Task<List<JsonNode>> Run(IEnumerable<string> replies, Func<IChatClient, Task> act)
    {
        using ScriptedHandler handler = new(replies);
        using HttpClient http = new(handler);
        OpenAIClientOptions options = new() { Transport = new HttpClientPipelineTransport(http) };
        using IChatClient inner = new OpenAIClient(new ApiKeyCredential("not-a-key"), options).GetChatClient("gpt-4o").AsIChatClient();
        using IChatClient client = new ChatClientBuilder(inner).UseFunctionInvocation().Build();

        await act(client);

        return handler.Requests.Select(body => JsonNode.Parse(body)!).ToList();
    }

    private static JsonNode SentSchema(JsonNode request) => request["response_format"]!["json_schema"]!["schema"]!;

    [Fact]
    public async Task A_schema_with_nothing_for_the_adapter_to_move_is_sent_unchanged()
    {
        ParseResult<JsonElement> result = default;

        List<JsonNode> requests = await Run([Completion("""{"title":"Fine","score":7,"note":null}""")], async client =>
            result = await client.GetResponseAsync(Plain, "Rate it.", profile: LlmProfiles.OpenAIStrict, schemaName: "answer", cancellationToken: Token));

        JsonNode sent = SentSchema(Assert.Single(requests));
        Assert.True(JsonNode.DeepEquals(Plain.Emit(LlmProfiles.OpenAIStrict).Schema, sent), sent.ToJsonString());
        Assert.Equal("answer", Assert.Single(requests)["response_format"]!["json_schema"]!["name"]!.GetValue<string>());
        Assert.True(result.IsSuccess);
        Assert.False(result.Value.TryGetProperty("note", out _));
    }

    [Fact]
    public async Task The_adapter_moves_rules_into_the_description_and_keeps_the_shape()
    {
        List<JsonNode> requests = await Run([Completion("{}")], async client =>
            await client.GetResponseAsync(Ruled, "Rate it.", profile: LlmProfiles.OpenAIStrict, cancellationToken: Token));

        JsonNode sent = SentSchema(Assert.Single(requests));
        JsonObject ours = Ruled.Emit(LlmProfiles.OpenAIStrict).Schema;

        // Rewritten again: pattern and the bounds are no longer keywords the provider sees.
        Assert.Null(sent["properties"]!["title"]!["pattern"]);
        Assert.Null(sent["properties"]!["score"]!["minimum"]);
        Assert.Null(sent["properties"]!["score"]!["maximum"]);
        Assert.Contains("minimum: 0", sent["properties"]!["score"]!["description"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("pattern: ^[A-Z]", sent["properties"]!["title"]!["description"]!.GetValue<string>(), StringComparison.Ordinal);

        // What matters for parsing is intact: the same properties, all required, closed, with the optional one nullable.
        Assert.Equal(ours["required"]!.ToJsonString(), sent["required"]!.ToJsonString());
        Assert.False(sent["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(ours["properties"]!["note"]!.ToJsonString(), sent["properties"]!["note"]!.ToJsonString());
        Assert.Equal("integer", sent["properties"]!["score"]!["type"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"title":"Fine","score":7,"note":null}""")]
    [InlineData("""{"title":"Fine","score":0,"note":"x"}""")]
    [InlineData("""{"title":"A","score":10,"note":null}""")]
    public async Task What_the_full_schema_accepts_the_sent_schema_accepts_too(string answer)
    {
        ParseResult<JsonElement> result = default;

        List<JsonNode> requests = await Run([Completion(answer)], async client =>
            result = await client.GetResponseAsync(Ruled, "Rate it.", profile: LlmProfiles.OpenAIStrict, cancellationToken: Token));

        JsonSchema sent = JsonSchema.FromText(SentSchema(Assert.Single(requests)).ToJsonString(), new BuildOptions { SchemaRegistry = new SchemaRegistry() });

        Assert.True(result.IsSuccess);
        Assert.True(sent.Evaluate(JsonElement.Parse(answer)).IsValid);
    }

    [Theory]
    [InlineData("""{"title":"lower","score":7,"note":null}""", "/title")]
    [InlineData("""{"title":"Fine","score":11,"note":null}""", "/score")]
    [InlineData("""{"title":"Fine","score":-1,"note":null}""", "/score")]
    public async Task A_rule_the_adapter_moved_is_still_enforced_on_the_answer(string answer, string path)
    {
        ParseResult<JsonElement> result = default;

        List<JsonNode> requests = await Run([Completion(answer)], async client =>
            result = await client.GetResponseAsync(Ruled, "Rate it.", profile: LlmProfiles.OpenAIStrict, cancellationToken: Token));

        // The provider is no longer asked to hold the rule, since the sent schema accepts the answer, so parsing has to.
        JsonSchema sent = JsonSchema.FromText(SentSchema(Assert.Single(requests)).ToJsonString(), new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        Assert.True(sent.Evaluate(JsonElement.Parse(answer)).IsValid);
        Assert.False(result.IsSuccess);
        Assert.Equal(path, result.Issues[0].InstanceLocation);
    }

    [Fact]
    public async Task A_wrapped_root_survives_the_adapter()
    {
        ArraySchema<string> list = Z.Array(Z.String()).MinItems(1);
        ParseResult<IReadOnlyList<string>> result = default;

        List<JsonNode> requests = await Run([Completion("""{"data":["a"]}""")], async client =>
            result = await client.GetResponseAsync(list, "List.", profile: LlmProfiles.OpenAIStrict, cancellationToken: Token));

        JsonNode sent = SentSchema(Assert.Single(requests));
        Assert.Equal("object", sent["type"]!.GetValue<string>());
        Assert.NotNull(sent["properties"]!["data"]);
        Assert.True(result.IsSuccess);
        Assert.Equal("a", Assert.Single(result.Value));
    }

    [Fact]
    public async Task A_tool_schema_with_nothing_to_move_is_sent_unchanged()
    {
        ZorgAIFunction function = ZorgAIFunction.Create("tool", "A tool.", Plain, _ => "ok", LlmProfiles.OpenAIStrict);

        List<JsonNode> requests = await Run([Completion("hi")], async client =>
            await client.GetResponseAsync("Go.", new ChatOptions { Tools = [function] }, Token));

        JsonNode parameters = Assert.Single(requests)["tools"]![0]!["function"]!["parameters"]!;
        Assert.True(JsonNode.DeepEquals(Plain.Emit(LlmProfiles.OpenAIStrict).Schema, parameters), parameters.ToJsonString());
    }

    [Fact]
    public async Task A_refused_call_goes_back_to_the_model_as_a_tool_result_it_can_read()
    {
        List<JsonElement> calls = [];
        ZorgAIFunction function = ZorgAIFunction.Create("tool", "A tool.", Ruled, arguments =>
        {
            calls.Add(arguments);
            return "ok";
        }, LlmProfiles.OpenAIStrict);
        ChatResponse? response = null;

        List<JsonNode> requests = await Run(
            [ToolCall("tool", """{"title":"Fine","score":99,"note":null}"""), Completion("Sorry, fixing.")],
            async client => response = await client.GetResponseAsync("Go.", new ChatOptions { Tools = [function] }, Token));

        Assert.Equal(2, requests.Count);
        Assert.Empty(calls);
        string toolMessage = requests[1]["messages"]!.AsArray().Single(message => message!["role"]!.GetValue<string>() == "tool")!["content"]!.ToString();
        JsonElement report = JsonElement.Parse(toolMessage);
        Assert.Equal("invalid_arguments", report.GetProperty("error").GetString());
        Assert.Equal("/score", report.GetProperty("issues")[0].GetProperty("path").GetString());
        Assert.Equal("Sorry, fixing.", response!.Text);
    }

    [Fact]
    public async Task A_valid_call_runs_the_function_and_a_null_for_an_optional_property_is_absent()
    {
        List<JsonElement> calls = [];
        ZorgAIFunction function = ZorgAIFunction.Create("tool", "A tool.", Ruled, arguments =>
        {
            calls.Add(arguments);
            return "ok";
        }, LlmProfiles.OpenAIStrict);

        List<JsonNode> requests = await Run(
            [ToolCall("tool", """{"title":"Fine","score":3,"note":null}"""), Completion("Done.")],
            async client => await client.GetResponseAsync("Go.", new ChatOptions { Tools = [function] }, Token));

        JsonElement call = Assert.Single(calls);
        Assert.Equal(3, call.GetProperty("score").GetInt32());
        Assert.False(call.TryGetProperty("note", out _));
        Assert.Equal(2, requests.Count);
    }

    [Fact]
    public async Task An_exception_from_the_function_is_left_to_the_function_invoking_client()
    {
        ZorgAIFunction function = ZorgAIFunction.Create("tool", "A tool.", Plain, new Func<JsonElement, object?>(_ => throw new InvalidOperationException("boom")), LlmProfiles.OpenAIStrict);

        List<JsonNode> requests = await Run(
            [ToolCall("tool", """{"title":"a","score":1,"note":null}"""), Completion("Done.")],
            async client => await client.GetResponseAsync("Go.", new ChatOptions { Tools = [function] }, Token));

        // By default the invoking client reports a failure without its detail. This is not our report, and not an invalid_arguments one.
        string toolMessage = requests[1]["messages"]!.AsArray().Single(message => message!["role"]!.GetValue<string>() == "tool")!["content"]!.ToString();
        Assert.DoesNotContain("invalid_arguments", toolMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("boom", toolMessage, StringComparison.Ordinal);
        Assert.Contains("rror", toolMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_detailed_errors_on_the_exception_text_reaches_the_model_untouched_by_us()
    {
        ZorgAIFunction function = ZorgAIFunction.Create("tool", "A tool.", Plain, new Func<JsonElement, object?>(_ => throw new InvalidOperationException("boom")), LlmProfiles.OpenAIStrict);
        using ScriptedHandler handler = new([ToolCall("tool", """{"title":"a","score":1,"note":null}"""), Completion("Done.")]);
        using HttpClient http = new(handler);
        OpenAIClientOptions options = new() { Transport = new HttpClientPipelineTransport(http) };
        using IChatClient inner = new OpenAIClient(new ApiKeyCredential("not-a-key"), options).GetChatClient("gpt-4o").AsIChatClient();
        using IChatClient client = new ChatClientBuilder(inner).UseFunctionInvocation(configure: invoking => invoking.IncludeDetailedErrors = true).Build();

        await client.GetResponseAsync("Go.", new ChatOptions { Tools = [function] }, Token);

        Assert.Contains("boom", handler.Requests[1], StringComparison.Ordinal);
    }

    private sealed class ScriptedHandler(IEnumerable<string> replies) : HttpMessageHandler
    {
        private readonly Queue<string> _replies = new(replies);

        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_replies.Dequeue(), Encoding.UTF8, "application/json"),
            };
        }
    }
}
