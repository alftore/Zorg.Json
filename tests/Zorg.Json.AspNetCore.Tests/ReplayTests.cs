using System;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Zorg.Json.AspNetCore.Tests;

public sealed class ReplayTests
{
    private static async Task<HttpResponseMessage> PostAsync(TestApp app, string path, byte[] body, string contentType)
    {
        using ByteArrayContent content = new(body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return await app.Client.PostAsync(new Uri(path, UriKind.Relative), content);
    }

    private static void Relaxed(IServiceCollection services) =>
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping);

    [Theory]
    [InlineData("application/json; charset=iso-8859-1")]
    [InlineData("application/json; charset=utf-16")]
    [InlineData("application/json; charset=bogus")]
    public async Task A_charset_other_than_utf8_is_refused_with_a_problem(string contentType)
    {
        ObjectSchema schema = Z.Object().Property("name", Z.String().MaxLength(1));
        int calls = 0;

        await using TestApp app = await TestApp.StartAsync(
            a => a.MapPost("/x", (JsonElement body) =>
            {
                calls++;
                return body.GetProperty("name").GetString()!.Length;
            }).ValidateBody(schema),
            Relaxed);

        HttpResponseMessage response = await PostAsync(app, "/x", Encoding.UTF8.GetBytes("""{"name":"é"}"""), contentType);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("application/json; charset=UTF-8")]
    [InlineData("application/json; charset=\"utf-8\"")]
    [InlineData("application/json")]
    public async Task Utf8_is_accepted_and_the_handler_sees_what_was_validated(string contentType)
    {
        ObjectSchema<Person> schema = PersonSchema.Create().Property(person => person.Name, s => s.MaxLength(1));

        await using TestApp app = await TestApp.StartAsync(a => a.MapPost("/x", (Person person) => person.Name.Length).ValidateBody(schema), Relaxed);

        HttpResponseMessage response = await PostAsync(app, "/x", Encoding.UTF8.GetBytes("""{"name":"é"}"""), contentType);

        Assert.Equal("1", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_absent_Optional_stays_absent_for_a_plain_parameter()
    {
        ObjectSchema<Patch> schema = Schema.Object(TestJson.Default.Patch);

        await using TestApp app = await TestApp.StartAsync(a =>
        {
            a.MapPost("/plain", (Patch patch) => patch.Nick.IsSet).ValidateBody(schema);
            a.MapPost("/validated", (Validated<Patch> patch) => patch.Value.Nick.IsSet).ValidateBody(schema);
        });

        Assert.Equal("false", await (await app.PostJsonAsync("/plain", "{}")).Content.ReadAsStringAsync());
        Assert.Equal("false", await (await app.PostJsonAsync("/validated", "{}")).Content.ReadAsStringAsync());
        Assert.Equal("true", await (await app.PostJsonAsync("/plain", """{"nick":null}""")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Strip_and_default_reach_a_plain_JsonElement_parameter()
    {
        ObjectSchema schema = Z.Object().Property("a", Z.Integer().Default(5)).Strip();

        await using TestApp app = await TestApp.StartAsync(a => a.MapPost("/x", (JsonElement body) => body.GetRawText()).ValidateBody(schema));

        Assert.Equal("""{"a":5}""", await (await app.PostJsonAsync("/x", """{"extra":1}""")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_lone_surrogate_in_a_validated_body_is_not_a_500()
    {
        ObjectSchema schema = Z.Object().Passthrough();

        await using TestApp app = await TestApp.StartAsync(a =>
        {
            a.MapPost("/plain", (JsonElement body) => "ok").ValidateBody(schema);
            a.MapPost("/validated", (Validated<JsonElement> body) => "ok").ValidateBody(schema);
        });

        HttpResponseMessage plain = await app.PostJsonAsync("/plain", """{"x":"\ud800"}""");
        HttpResponseMessage validated = await app.PostJsonAsync("/validated", """{"x":"\ud800"}""");

        Assert.Equal(HttpStatusCode.OK, validated.StatusCode);
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
    }

    [Fact]
    public async Task Many_issues_get_a_small_response()
    {
        ArraySchema<string> schema = Z.Array(Z.String());
        string body = "[" + string.Join(",", Enumerable.Repeat("1", 20000)) + "]";

        await using TestApp app = await TestApp.StartAsync(a => a.MapPost("/x", (Validated<string[]> items) => "ok").ValidateBody(schema));

        HttpResponseMessage response = await app.PostJsonAsync("/x", body);
        string text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(text.Length < 64 * 1024, $"The response was {text.Length} characters.");
        Assert.Contains("too_many_issues", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_client_text_is_cut()
    {
        string key = new('k', 5000);
        ImmutableArray<ParseIssue> issues =
        [
            new ParseIssue { Code = "x", Message = $"Unknown property {key}.", InstanceLocation = "/" + key },
        ];

        HttpValidationProblemDetails details = issues.ToProblemDetails();

        foreach ((string pointer, string[] messages) in details.Errors)
        {
            Assert.True(pointer.Length < 400);
            Assert.All(messages, message => Assert.True(message.Length < 400));
        }
    }
}
