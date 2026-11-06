using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Xunit;
using Zorg.Json.Emission;
using Zorg.Json.Llm;

namespace Zorg.Json.Extensions.AI.Tests;

public sealed record Forecast(string City, int Days);

[JsonSerializable(typeof(Forecast))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
public sealed partial class ForecastJson : JsonSerializerContext;

public sealed class ZorgAIFunctionTests
{
    private static readonly ObjectSchema Arguments = Z.Object()
        .Property("city", Z.String().MinLength(1))
        .Property("days", Z.Integer().Minimum(1).Maximum(14));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static AIFunctionArguments Args(string json)
    {
        AIFunctionArguments arguments = [];

        foreach (JsonProperty property in JsonElement.Parse(json).EnumerateObject())
        {
            arguments[property.Name] = property.Value.Clone();
        }

        return arguments;
    }

    private static ZorgAIFunction Counting(Schema<JsonElement> schema, out List<JsonElement> calls, JsonSchemaProfile? profile = null)
    {
        List<JsonElement> seen = [];
        calls = seen;

        return ZorgAIFunction.Create("tool", "A tool.", schema, arguments =>
        {
            seen.Add(arguments);
            return "done";
        }, profile);
    }

    private static JsonElement AsJson(object? result) => Assert.IsType<JsonElement>(result);

    [Fact]
    public void Exposes_name_description_and_the_emitted_schema()
    {
        ZorgAIFunction function = Counting(Arguments, out _);

        Assert.Equal("tool", function.Name);
        Assert.Equal("A tool.", function.Description);
        Assert.Equal("object", function.JsonSchema.GetProperty("type").GetString());
        Assert.True(function.JsonSchema.GetProperty("properties").TryGetProperty("city", out _));
        Assert.Equal(1, function.JsonSchema.GetProperty("properties").GetProperty("days").GetProperty("minimum").GetInt32());
    }

    [Fact]
    public async Task Valid_arguments_reach_the_function_and_its_result_comes_back()
    {
        ZorgAIFunction function = Counting(Arguments, out List<JsonElement> calls);

        object? result = await function.InvokeAsync(Args("""{"city":"Oslo","days":3}"""), Token);

        Assert.Equal("done", result);
        JsonElement call = Assert.Single(calls);
        Assert.Equal("Oslo", call.GetProperty("city").GetString());
        Assert.Equal(3, call.GetProperty("days").GetInt32());
    }

    [Fact]
    public async Task Plain_dotnet_values_are_read_as_json()
    {
        ZorgAIFunction function = Counting(Arguments, out List<JsonElement> calls);
        AIFunctionArguments arguments = new() { ["city"] = "Oslo", ["days"] = 3 };

        object? result = await function.InvokeAsync(arguments, Token);

        Assert.Equal("done", result);
        Assert.Equal(3, Assert.Single(calls).GetProperty("days").GetInt32());
    }

    [Fact]
    public async Task A_typed_schema_gives_the_function_a_typed_value()
    {
        ObjectSchema<Forecast> schema = Schema.Object(ForecastJson.Default.Forecast).Property(f => f.Days, s => s.Minimum(1));
        ZorgAIFunction function = ZorgAIFunction.Create("forecast", "Gets one.", schema, forecast => $"{forecast.City} for {forecast.Days}");

        object? ok = await function.InvokeAsync(Args("""{"city":"Oslo","days":2}"""), Token);
        JsonElement refused = AsJson(await function.InvokeAsync(Args("""{"city":"Oslo","days":0}"""), Token));

        Assert.Equal("Oslo for 2", ok);
        Assert.Equal("/days", refused.GetProperty("issues")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task The_async_overload_receives_the_token()
    {
        using CancellationTokenSource source = new();
        CancellationToken seen = default;
        ZorgAIFunction function = ZorgAIFunction.Create("tool", "", Arguments, (JsonElement _, CancellationToken token) =>
        {
            seen = token;
            return ValueTask.FromResult<object?>(42);
        });

        object? result = await function.InvokeAsync(Args("""{"city":"a","days":1}"""), source.Token);

        Assert.Equal(42, result);
        Assert.Equal(source.Token, seen);
    }

    [Theory]
    [InlineData("""{"city":"Oslo","days":0}""", "/days", "too_small")]
    [InlineData("""{"city":"Oslo","days":15}""", "/days", "too_big")]
    [InlineData("""{"city":"","days":3}""", "/city", "too_small")]
    [InlineData("""{"city":"Oslo","days":"three"}""", "/days", "invalid_type")]
    [InlineData("""{"city":"Oslo"}""", "/days", "required")]
    [InlineData("""{"days":3}""", "/city", "required")]
    [InlineData("""{"city":null,"days":3}""", "/city", "invalid_type")]
    [InlineData("""{"city":"Oslo","days":3,"extra":1}""", "", "unrecognized_keys")]
    [InlineData("""{"city":"Oslo","days":2.5}""", "/days", "invalid_type")]
    public async Task Invalid_arguments_return_a_structured_error_and_skip_the_function(string json, string path, string code)
    {
        ZorgAIFunction function = Counting(Arguments, out List<JsonElement> calls);

        JsonElement report = AsJson(await function.InvokeAsync(Args(json), Token));

        Assert.Empty(calls);
        Assert.Equal("invalid_arguments", report.GetProperty("error").GetString());
        Assert.False(string.IsNullOrWhiteSpace(report.GetProperty("message").GetString()));
        Assert.Contains(report.GetProperty("issues").EnumerateArray(), issue =>
            issue.GetProperty("path").GetString() == path && issue.GetProperty("code").GetString() == code);
    }

    [Fact]
    public async Task No_arguments_at_all_is_a_refusal_not_an_exception()
    {
        ZorgAIFunction function = Counting(Arguments, out List<JsonElement> calls);

        JsonElement report = AsJson(await function.InvokeAsync(new AIFunctionArguments(), Token));

        Assert.Empty(calls);
        Assert.Equal("invalid_arguments", report.GetProperty("error").GetString());
    }

    [Fact]
    public async Task The_report_caps_its_issues_and_counts_the_rest()
    {
        ObjectSchema wide = Enumerable.Range(0, 30).Aggregate(Z.Object(), (schema, index) => schema.Property($"p{index}", Z.String()));
        ZorgAIFunction function = Counting(wide, out _);

        JsonElement report = AsJson(await function.InvokeAsync(Args("{}"), Token));

        Assert.Equal(20, report.GetProperty("issues").GetArrayLength());
        Assert.Equal(10, report.GetProperty("omitted").GetInt32());
    }

    [Fact]
    public async Task A_lone_surrogate_in_a_value_is_refused_without_throwing()
    {
        ZorgAIFunction function = Counting(Arguments, out List<JsonElement> calls);

        JsonElement report = AsJson(await function.InvokeAsync(Args("""{"city":"\ud800","days":3}"""), Token));

        Assert.Empty(calls);
        Assert.Equal("invalid_arguments", report.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_lone_surrogate_in_a_dotnet_string_does_not_throw_and_reaches_the_function_as_a_replacement_character()
    {
        // System.Text.Json writes U+FFFD for it, as it does everywhere else. A model's own escape is refused (above).
        ZorgAIFunction function = Counting(Arguments, out List<JsonElement> calls);
        AIFunctionArguments arguments = new() { ["city"] = "bad" + (char)0xD800, ["days"] = 3 };

        object? result = await function.InvokeAsync(arguments, Token);

        Assert.Equal("done", result);
        Assert.Equal("bad�", Assert.Single(calls).GetProperty("city").GetString());
    }

    [Fact]
    public async Task A_lone_surrogate_in_an_argument_name_is_refused_and_the_report_can_be_serialized()
    {
        ZorgAIFunction function = Counting(Arguments, out List<JsonElement> calls);
        AIFunctionArguments arguments = Args("""{"city":"Oslo","days":3}""");
        arguments["bad" + (char)0xD800] = 1;

        object? result = await function.InvokeAsync(arguments, Token);

        Assert.Empty(calls);

        // The chat client serializes whatever a function returns.
        JsonElement serialized = JsonSerializer.SerializeToElement(result, AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(object)));
        Assert.Equal("invalid_arguments", serialized.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_value_with_no_json_form_is_refused_without_throwing()
    {
        ZorgAIFunction function = Counting(Arguments, out List<JsonElement> calls);
        AIFunctionArguments arguments = new() { ["city"] = new Func<int>(() => 1), ["days"] = 3 };

        JsonElement report = AsJson(await function.InvokeAsync(arguments, Token));

        Assert.Empty(calls);
        Assert.Equal("invalid_arguments", report.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Arguments_nested_beyond_the_json_limit_are_refused_without_throwing()
    {
        ZorgAIFunction function = Counting(Arguments, out List<JsonElement> calls);
        object nested = "x";

        for (int depth = 0; depth < 200; depth++)
        {
            nested = new List<object> { nested };
        }

        AIFunctionArguments arguments = new() { ["city"] = nested, ["days"] = 3 };

        JsonElement report = AsJson(await function.InvokeAsync(arguments, Token));

        Assert.Empty(calls);
        Assert.Equal("invalid_arguments", report.GetProperty("error").GetString());
    }

    [Fact]
    public async Task An_exception_from_the_function_passes_through()
    {
        ZorgAIFunction function = ZorgAIFunction.Create("tool", "", Arguments, new Func<JsonElement, object?>(_ => throw new InvalidOperationException("boom")));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await function.InvokeAsync(Args("""{"city":"a","days":1}"""), Token));

        Assert.Equal("boom", error.Message);
    }

    [Fact]
    public async Task An_exception_from_a_refinement_passes_through()
    {
        ObjectSchema schema = Z.Object().Property("n", Z.Integer().Refine(_ => throw new NotSupportedException("refine"), "never"));
        ZorgAIFunction function = Counting(schema, out _);

        await Assert.ThrowsAsync<NotSupportedException>(async () => await function.InvokeAsync(Args("""{"n":1}"""), Token));
    }

    [Fact]
    public async Task A_json_exception_from_the_function_is_not_mistaken_for_bad_arguments()
    {
        ZorgAIFunction function = ZorgAIFunction.Create("tool", "", Arguments, new Func<JsonElement, object?>(_ => throw new JsonException("mine")));

        await Assert.ThrowsAsync<JsonException>(async () => await function.InvokeAsync(Args("""{"city":"a","days":1}"""), Token));
    }

    [Fact]
    public async Task A_cancelled_token_throws_before_the_function_runs()
    {
        ZorgAIFunction function = Counting(Arguments, out List<JsonElement> calls);
        using CancellationTokenSource source = new();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await function.InvokeAsync(Args("""{"city":"a","days":1}"""), source.Token));

        Assert.Empty(calls);
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
        ZorgAIFunction function = Counting(schema, out List<JsonElement> calls);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await function.InvokeAsync(Args("""{"name":"a"}"""), source.Token));

        Assert.Empty(calls);
    }

    [Fact]
    public async Task An_async_refinement_that_fails_is_reported_to_the_model()
    {
        ObjectSchema schema = Z.Object().Property("name", Z.String().RefineAsync((name, _) => ValueTask.FromResult(name != "taken"), "That name is taken"));
        ZorgAIFunction function = Counting(schema, out List<JsonElement> calls);

        JsonElement report = AsJson(await function.InvokeAsync(Args("""{"name":"taken"}"""), Token));
        object? fine = await function.InvokeAsync(Args("""{"name":"free"}"""), Token);

        Assert.Equal("/name", report.GetProperty("issues")[0].GetProperty("path").GetString());
        Assert.Contains("taken", report.GetProperty("issues")[0].GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("done", fine);
        Assert.Single(calls);
    }

    [Fact]
    public async Task A_strict_profile_sends_optional_properties_as_nullable_and_a_null_means_absent()
    {
        ObjectSchema schema = Z.Object().Property("city", Z.String()).Property("note", Z.String().Optional());
        ZorgAIFunction function = Counting(schema, out List<JsonElement> calls, LlmProfiles.OpenAIStrict);

        object? result = await function.InvokeAsync(Args("""{"city":"Oslo","note":null}"""), Token);

        Assert.Equal("done", result);
        Assert.False(Assert.Single(calls).TryGetProperty("note", out _));
        Assert.Equal(2, function.JsonSchema.GetProperty("required").GetArrayLength());
        Assert.False(function.JsonSchema.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void The_root_must_be_an_object()
    {
        Assert.Throws<ArgumentException>(() => ZorgAIFunction.Create<IReadOnlyList<string>>("tool", "", Z.Array(Z.String()), _ => "x"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void The_name_must_not_be_empty(string name)
    {
        Assert.Throws<ArgumentException>(() => ZorgAIFunction.Create(name, "", Arguments, _ => "x"));
    }

    [Fact]
    public void Null_arguments_are_refused_where_they_are_a_mistake()
    {
        Assert.Throws<ArgumentNullException>(() => ZorgAIFunction.Create("tool", "", (Schema<JsonElement>)null!, _ => "x"));
        Assert.Throws<ArgumentNullException>(() => ZorgAIFunction.Create("tool", null!, Arguments, _ => "x"));
        Assert.Throws<ArgumentNullException>(() => ZorgAIFunction.Create("tool", "", Arguments, (Func<JsonElement, object?>)null!));
    }

    [Fact]
    public void It_is_an_ordinary_function_declaration_for_a_chat_client()
    {
        AIFunction function = Counting(Arguments, out _);
        ChatOptions options = new() { Tools = [function] };

        Assert.Same(function, Assert.Single(options.Tools));
        Assert.IsAssignableFrom<AIFunctionDeclaration>(function);
        Assert.Equal(function.JsonSchema.GetRawText(), function.AsDeclarationOnly().JsonSchema.GetRawText());
    }
}
