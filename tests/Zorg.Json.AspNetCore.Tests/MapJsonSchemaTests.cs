using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;
using Zorg.Json.Emission;

namespace Zorg.Json.AspNetCore.Tests;

public sealed class MapJsonSchemaTests
{
    private static readonly StringSchema Name = Z.String().MinLength(1).Pattern(@"^\+");

    private static readonly Uri Path = new("/schemas/name.json", UriKind.Relative);

    private static Task<TestApp> AppAsync(JsonSchemaOptions? options = null) =>
        TestApp.StartAsync(a => a.MapJsonSchema("/schemas/name.json", Name, options));

    private static async Task<HttpResponseMessage> SendAsync(TestApp app, HttpMethod method, string? ifNoneMatch = null)
    {
        using HttpRequestMessage request = new(method, Path);

        if (ifNoneMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        return await app.Client.SendAsync(request);
    }

    [Fact]
    public async Task Serves_the_emitted_schema_as_schema_json()
    {
        await using TestApp app = await AppAsync();

        HttpResponseMessage response = await app.Client.GetAsync(Path);

        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/schema+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Name.ToJsonSchema().ToJsonString(), JsonNode.Parse(body)!.ToJsonString());
        Assert.Contains(@"^\\+", body, StringComparison.Ordinal);
        Assert.True(response.Headers.CacheControl?.NoCache);
    }

    [Fact]
    public async Task Options_choose_the_target()
    {
        await using TestApp app = await AppAsync(new JsonSchemaOptions { Target = JsonSchemaProfile.Draft07 });

        JsonElement schema = JsonElement.Parse(await app.Client.GetStringAsync(Path));

        Assert.Equal("http://json-schema.org/draft-07/schema#", schema.GetProperty("$schema").GetString());
    }

    [Fact]
    public async Task ETag_is_strong_and_stable()
    {
        await using TestApp app = await AppAsync();

        EntityTagHeaderValue? first = (await app.Client.GetAsync(Path)).Headers.ETag;
        EntityTagHeaderValue? second = (await app.Client.GetAsync(Path)).Headers.ETag;

        Assert.NotNull(first);
        Assert.False(first.IsWeak);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Different_documents_have_different_ETags()
    {
        await using TestApp app = await TestApp.StartAsync(a =>
        {
            a.MapJsonSchema("/a", Z.String());
            a.MapJsonSchema("/b", Z.String().MinLength(1));
        });

        EntityTagHeaderValue? a = (await app.Client.GetAsync(new Uri("/a", UriKind.Relative))).Headers.ETag;
        EntityTagHeaderValue? b = (await app.Client.GetAsync(new Uri("/b", UriKind.Relative))).Headers.ETag;

        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData("{0}")]
    [InlineData("W/{0}")]
    [InlineData("\"other\", {0}")]
    [InlineData("*")]
    public async Task Matching_If_None_Match_is_a_304_with_no_body(string format)
    {
        await using TestApp app = await AppAsync();
        string tag = (await app.Client.GetAsync(Path)).Headers.ETag!.Tag;

        HttpResponseMessage response = await SendAsync(app, HttpMethod.Get, string.Format(System.Globalization.CultureInfo.InvariantCulture, format, tag));

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(tag, response.Headers.ETag?.Tag);
        Assert.True(response.Headers.CacheControl?.NoCache);
    }

    [Fact]
    public async Task Other_If_None_Match_gets_the_document()
    {
        await using TestApp app = await AppAsync();

        HttpResponseMessage response = await SendAsync(app, HttpMethod.Get, "\"stale\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Head_has_the_headers_and_no_body()
    {
        await using TestApp app = await AppAsync();
        long? length = (await app.Client.GetAsync(Path)).Content.Headers.ContentLength;

        HttpResponseMessage response = await SendAsync(app, HttpMethod.Head);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(length, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Post_is_not_mapped()
    {
        await using TestApp app = await AppAsync();

        HttpResponseMessage response = await app.PostJsonAsync("/schemas/name.json", "{}");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Emission_failure_stops_startup_instead_of_the_first_request()
    {
        JsonSchemaOptions strict = new() { Unrepresentable = UnrepresentablePolicy.Throw };

        await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(a =>
            a.MapJsonSchema("/x", Z.String().Refine(text => text.Length > 0, "Not empty"), strict)));
    }

    [Fact]
    public async Task Reference_prefix_with_definitions_is_rejected()
    {
        ObjectSchema schema = Z.Object().Property("email", Z.String().Email().Named("email"));
        JsonSchemaOptions external = new() { Reused = ReusedSchemaHandling.Ref, ReferencePrefix = "#/components/schemas/" };

        await Assert.ThrowsAsync<ArgumentException>(() => TestApp.StartAsync(a => a.MapJsonSchema("/x", schema, external)));
    }
}
