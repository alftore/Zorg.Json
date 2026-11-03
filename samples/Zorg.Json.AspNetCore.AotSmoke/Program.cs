using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zorg.Json;
using Zorg.Json.AspNetCore;
using Zorg.Json.AspNetCore.OpenApi;

// Runs the package over real HTTP in a Native AOT binary. Any wrong result makes the exit code non-zero.
ObjectSchema person = Z.Object()
    .Property("name", Z.String().Trim().MinLength(1))
    .Property("age", Z.Integer().Minimum(0).Default(18));

int failures = 0;

// Problem responses take a different path with and without AddProblemDetails.
await RunAsync(addProblemDetails: false);
await RunAsync(addProblemDetails: true);

Console.WriteLine(failures == 0 ? "ASP.NET Core AOT smoke test passed" : $"ASP.NET Core AOT smoke test failed: {failures} check(s)");
return failures == 0 ? 0 : 1;

async Task RunAsync(bool addProblemDetails)
{
    WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, SmokeJson.Default));
    builder.Services.AddOptions<SmokeOptions>()
        .Configure(options => options.Port = 25)
        .ValidateWithSchema(Z.Object().Property("port", Z.Integer().Minimum(1)), SmokeJson.Default.SmokeOptions)
        .ValidateOnStart();

    if (addProblemDetails)
    {
        builder.Services.AddProblemDetails();
    }

    builder.Services.AddOpenApi(options => options.AddEndpointSchemas());

    WebApplication app = builder.Build();

    app.MapPost("/plain", (JsonElement body) => body.GetProperty("name").GetString() ?? "").ValidateBody(person);
    app.MapPost("/validated", (Validated<JsonElement> body) => body.Value.GetProperty("age").GetInt64()).ValidateBody(person);
    app.MapPost("/count", ([FromBody] long count) => count + 1).ValidateBody(Z.Integer());
    app.MapJsonSchema("/schemas/person.json", person);
    app.MapOpenApi();

    await app.StartAsync();

    string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    using HttpClient client = new() { BaseAddress = new Uri(address) };

    await CheckBody("plain parameter", "/plain", """{ "name": " Ada " }""", HttpStatusCode.OK, "Ada");
    await CheckBody("Validated parameter", "/validated", """{ "name": "Ada" }""", HttpStatusCode.OK, "18");
    await CheckBody("value the binder can't read", "/count", "1.0", HttpStatusCode.OK, "2");
    await CheckBody("invalid body", "/plain", """{ "name": "", "age": -1 }""", HttpStatusCode.BadRequest, "\"/age\":[\"too_small\"]");
    await CheckBody("malformed body", "/validated", "{", HttpStatusCode.BadRequest, "invalid_json");

    using HttpResponseMessage schema = await client.GetAsync(new Uri("/schemas/person.json", UriKind.Relative));
    Check("schema media type", schema.Content.Headers.ContentType?.MediaType == "application/schema+json");
    Check("schema document", (await schema.Content.ReadAsStringAsync()).Contains("\"minimum\":0", StringComparison.Ordinal));

    using HttpRequestMessage conditional = new(HttpMethod.Get, new Uri("/schemas/person.json", UriKind.Relative));
    conditional.Headers.IfNoneMatch.Add(schema.Headers.ETag!);
    using HttpResponseMessage notModified = await client.SendAsync(conditional);
    Check("If-None-Match", notModified.StatusCode == HttpStatusCode.NotModified);

    // The document describes the 400 response, and AddProblemDetails is what gives that type JSON metadata under AOT.
    if (addProblemDetails)
    {
        string openApi = await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        Check("OpenAPI request body", openApi.Contains("\"minimum\": 0", StringComparison.Ordinal), openApi);
    }

    Check("options", app.Services.GetRequiredService<IOptions<SmokeOptions>>().Value.Port == 25);

    await app.StopAsync();

    async Task CheckBody(string name, string path, string json, HttpStatusCode status, string expected)
    {
        using StringContent content = new(json, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync(new Uri(path, UriKind.Relative), content);
        string body = await response.Content.ReadAsStringAsync();
        Check(name, response.StatusCode == status && body.Contains(expected, StringComparison.Ordinal), $"{(int)response.StatusCode} {body}");
    }
}

void Check(string name, bool passed, string? detail = null)
{
    if (!passed)
    {
        failures++;
        Console.Error.WriteLine($"FAILED: {name} {detail}");
    }
}

internal sealed class SmokeOptions
{
    public int Port { get; set; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(SmokeOptions))]
internal sealed partial class SmokeJson : JsonSerializerContext;
