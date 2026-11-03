using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Zorg.Json.AspNetCore.Tests;

/// <summary>A real pipeline on the in-memory test server.</summary>
internal sealed class TestApp : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestApp(WebApplication app)
    {
        _app = app;
        Client = app.GetTestClient();
    }

    public HttpClient Client { get; }

    public IServiceProvider Services => _app.Services;

    public static async Task<TestApp> StartAsync(
        Action<WebApplication> map,
        Action<IServiceCollection>? services = null,
        string environment = "Production")
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, TestJson.Default));
        services?.Invoke(builder.Services);

        WebApplication app = builder.Build();
        map(app);
        await app.StartAsync();
        return new TestApp(app);
    }

    public static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    public async Task<HttpResponseMessage> PostJsonAsync(string path, string json)
    {
        using StringContent content = Json(json);
        return await Client.PostAsync(new Uri(path, UriKind.Relative), content);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }
}

internal sealed record Person(string Name, int Age);

internal sealed record Reply(string Text);

internal sealed class SmtpOptions
{
    public string Host { get; set; } = "";

    public int Port { get; set; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(Person))]
[JsonSerializable(typeof(Reply))]
[JsonSerializable(typeof(SmtpOptions))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class TestJson : JsonSerializerContext;

internal static class PersonSchema
{
    public static ObjectSchema<Person> Create() => Schema.Object(TestJson.Default.Person)
        .Property(person => person.Name, s => s.Trim().MinLength(1))
        .Property(person => person.Age, s => s.Minimum(0).Default(18));
}
