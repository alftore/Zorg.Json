using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Zorg.Json.AspNetCore.Tests;

public sealed class ValidateBodyTests
{
    private static readonly ObjectSchema<Person> People = PersonSchema.Create();

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return JsonElement.Parse(await response.Content.ReadAsStringAsync());
    }

    private static Task<TestApp> PeopleAppAsync(string environment = "Production") => TestApp.StartAsync(
        app => app.MapPost("/people", (Person person) => new Reply($"{person.Name}:{person.Age}")).ValidateBody(People),
        environment: environment);

    [Fact]
    public async Task Plain_parameter_gets_the_validated_value()
    {
        await using TestApp app = await PeopleAppAsync();

        HttpResponseMessage response = await app.PostJsonAsync("/people", """{ "name": "Ada", "age": 36 }""");

        Assert.Equal("""{"text":"Ada:36"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Plain_parameter_sees_normalization_and_defaults()
    {
        await using TestApp app = await PeopleAppAsync();

        HttpResponseMessage response = await app.PostJsonAsync("/people", """{ "name": "  Ada  " }""");

        Assert.Equal("""{"text":"Ada:18"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Invalid_body_gets_issues_by_json_pointer_with_codes()
    {
        await using TestApp app = await PeopleAppAsync();

        HttpResponseMessage response = await app.PostJsonAsync("/people", """{ "name": "", "age": -1 }""");

        JsonElement problem = await ProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Equal("One or more validation errors occurred.", problem.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Array, problem.GetProperty("errors").GetProperty("/name").ValueKind);
        Assert.Equal(JsonValueKind.Array, problem.GetProperty("errors").GetProperty("/age").ValueKind);
        Assert.Equal("too_small", problem.GetProperty("codes").GetProperty("/name")[0].GetString());
        Assert.Equal("too_small", problem.GetProperty("codes").GetProperty("/age")[0].GetString());
    }

    [Fact]
    public async Task Wrong_type_reaches_the_schema_and_not_the_binder()
    {
        await using TestApp app = await PeopleAppAsync();

        HttpResponseMessage response = await app.PostJsonAsync("/people", """{ "name": "Ada", "age": "old" }""");

        JsonElement problem = await ProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("invalid_type", problem.GetProperty("codes").GetProperty("/age")[0].GetString());
    }

    [Theory]
    [InlineData("{ \"name\": ")]
    [InlineData("")]
    public async Task Malformed_body_is_a_400_problem(string body)
    {
        await using TestApp app = await PeopleAppAsync();

        HttpResponseMessage response = await app.PostJsonAsync("/people", body);

        JsonElement problem = await ProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("invalid_json", problem.GetProperty("codes").GetProperty("")[0].GetString());
    }

    [Fact]
    public async Task Malformed_body_is_a_400_problem_in_development_too()
    {
        // The framework's binder throws here instead, because ThrowOnBadRequest defaults to true in Development.
        await using TestApp app = await PeopleAppAsync("Development");

        HttpResponseMessage response = await app.PostJsonAsync("/people", "{");

        await ProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Duplicate_keys_are_malformed()
    {
        await using TestApp app = await PeopleAppAsync();

        HttpResponseMessage response = await app.PostJsonAsync("/people", """{ "name": "Ada", "name": "Eve" }""");

        JsonElement problem = await ProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("invalid_json", problem.GetProperty("codes").GetProperty("")[0].GetString());
    }

    [Fact]
    public async Task Non_json_body_is_a_415_from_routing_when_the_handler_declares_a_body()
    {
        // Accepts metadata makes the routing matcher answer, before the endpoint runs, with an empty body.
        bool called = false;
        await using TestApp app = await TestApp.StartAsync(a => a
            .MapPost("/people", (Validated<Person> person) =>
            {
                called = true;
                return person.Value.Name;
            })
            .ValidateBody(People));

        using StringContent content = new("name=Ada", Encoding.UTF8, "text/plain");
        HttpResponseMessage response = await app.Client.PostAsync(new Uri("/people", UriKind.Relative), content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.False(called);
    }

    [Fact]
    public async Task Non_json_body_is_a_415_problem_when_the_handler_declares_no_body()
    {
        bool called = false;
        await using TestApp app = await TestApp.StartAsync(a => a
            .MapPost("/hook", () => called = true)
            .ValidateBody(People));

        using StringContent content = new("name=Ada", Encoding.UTF8, "text/plain");
        HttpResponseMessage response = await app.Client.PostAsync(new Uri("/hook", UriKind.Relative), content);

        await ProblemAsync(response, HttpStatusCode.UnsupportedMediaType);
        Assert.False(called);
    }

    [Fact]
    public async Task Json_suffix_media_types_are_accepted()
    {
        await using TestApp app = await PeopleAppAsync();

        using StringContent content = new("""{ "name": "Ada" }""", Encoding.UTF8, "application/vnd.api+json");
        HttpResponseMessage response = await app.Client.PostAsync(new Uri("/people", UriKind.Relative), content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Body_over_the_size_limit_keeps_its_status()
    {
        await using TestApp app = await TestApp.StartAsync(a =>
        {
            a.Use((context, next) =>
            {
                context.Features.Set<IHttpMaxRequestBodySizeFeature>(null);
                context.Request.Body = new ThrowingStream();
                return next();
            });
            a.MapPost("/people", (Validated<Person> person) => person.Value.Name).ValidateBody(People);
        });

        HttpResponseMessage response = await app.PostJsonAsync("/people", "{}");

        await ProblemAsync(response, HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Validated_parameter_gets_the_parsed_value()
    {
        await using TestApp app = await TestApp.StartAsync(a => a
            .MapPost("/people", (Validated<Person> person) => new Reply($"{person.Value.Name}:{person.Value.Age}"))
            .ValidateBody(People));

        HttpResponseMessage response = await app.PostJsonAsync("/people", """{ "name": " Ada " }""");

        Assert.Equal("""{"text":"Ada:18"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Validated_parameter_is_the_instance_the_schema_produced()
    {
        Person? seen = null;
        await using TestApp app = await TestApp.StartAsync(a => a
            .MapPost("/people", (HttpContext context, Validated<Person> person) =>
            {
                seen = context.Features.Get<Validated<Person>>()?.Value;
                return ReferenceEquals(seen, person.Value);
            })
            .ValidateBody(People));

        HttpResponseMessage response = await app.PostJsonAsync("/people", """{ "name": "Ada" }""");

        Assert.Equal("true", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Validated_parameter_without_ValidateBody_fails_with_a_clear_message()
    {
        await using TestApp app = await TestApp.StartAsync(a =>
        {
            string? message = null;
            a.Use(async (context, next) =>
            {
                try
                {
                    await next();
                }
                catch (InvalidOperationException exception)
                {
                    message = exception.Message;
                    await context.Response.WriteAsync(message);
                }
            });
            a.MapPost("/people", (Validated<Person> person) => person.Value.Name);
        });

        HttpResponseMessage response = await app.PostJsonAsync("/people", "{}");

        Assert.Contains("ValidateBody", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Untyped_object_schema_binds_a_JsonElement()
    {
        await using TestApp app = await TestApp.StartAsync(a => a
            .MapPost("/search", (JsonElement body) => body.GetProperty("limit").GetInt32() + 1)
            .ValidateBody(Z.Object().Strict()
                .Property("query", Z.String().MinLength(1))
                .Property("limit", Z.Integer().Default(10))));

        HttpResponseMessage valid = await app.PostJsonAsync("/search", """{ "query": "zorg" }""");
        HttpResponseMessage invalid = await app.PostJsonAsync("/search", """{ "query": "", "extra": 1 }""");

        Assert.Equal("11", await valid.Content.ReadAsStringAsync());
        JsonElement problem = await ProblemAsync(invalid, HttpStatusCode.BadRequest);
        Assert.Equal("too_small", problem.GetProperty("codes").GetProperty("/query")[0].GetString());
        Assert.Equal("unrecognized_keys", problem.GetProperty("codes").GetProperty("")[0].GetString());
    }

    [Fact]
    public async Task Input_the_binder_would_reject_still_binds()
    {
        // STJ can't read 1.0 as a long, and Zorg can.
        await using TestApp app = await TestApp.StartAsync(a => a
            .MapPost("/count", ([FromBody] long count) => count + 1)
            .ValidateBody(Z.Integer()));

        HttpResponseMessage response = await app.PostJsonAsync("/count", "1.0");

        Assert.Equal("2", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Route_value_of_the_same_type_is_left_alone()
    {
        await using TestApp app = await TestApp.StartAsync(a => a
            .MapPost("/rename/{id}", (string id, [FromBody] string name) => $"{id}={name}")
            .ValidateBody(Z.String().Trim()));

        HttpResponseMessage response = await app.PostJsonAsync("/rename/7", "\" ada \"");

        Assert.Equal("7=ada", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Handler_without_a_body_parameter_can_read_the_original_bytes()
    {
        await using TestApp app = await TestApp.StartAsync(a => a
            .MapPost("/hook", async (HttpRequest request) =>
            {
                using StreamReader reader = new(request.Body);
                return await reader.ReadToEndAsync();
            })
            .ValidateBody(Z.Object().Property("name", Z.String().Trim())));

        HttpResponseMessage response = await app.PostJsonAsync("/hook", """{ "name": " Ada " }""");

        Assert.Equal("""{ "name": " Ada " }""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Async_refinements_run()
    {
        await using TestApp app = await TestApp.StartAsync(a => a
            .MapPost("/names", ([FromBody] string name) => name)
            .ValidateBody(Z.String().RefineAsync((name, _) => ValueTask.FromResult(name != "taken"), "This name is taken")));

        HttpResponseMessage response = await app.PostJsonAsync("/names", "\"taken\"");

        JsonElement problem = await ProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("custom", problem.GetProperty("codes").GetProperty("")[0].GetString());
    }

    [Fact]
    public async Task Endpoint_filters_run_after_validation()
    {
        int filtered = 0;
        await using TestApp app = await TestApp.StartAsync(a => a
            .MapPost("/people", (Person person) => person.Name)
            .ValidateBody(People)
            .AddEndpointFilter((context, next) =>
            {
                filtered++;
                Assert.Equal("Ada", context.GetArgument<Person>(0).Name);
                return next(context);
            }));

        await app.PostJsonAsync("/people", """{ "name": "" }""");
        Assert.Equal(0, filtered);

        await app.PostJsonAsync("/people", """{ "name": " Ada " }""");
        Assert.Equal(1, filtered);
    }

    [Fact]
    public async Task Problem_details_service_customizations_apply()
    {
        await using TestApp app = await TestApp.StartAsync(
            a => a.MapPost("/people", (Person person) => person.Name).ValidateBody(People),
            services => services.AddProblemDetails(options =>
                options.CustomizeProblemDetails = context => context.ProblemDetails.Extensions["node"] = "test"));

        HttpResponseMessage response = await app.PostJsonAsync("/people", "{}");

        JsonElement problem = await ProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("test", problem.GetProperty("node").GetString());
        Assert.Equal("required", problem.GetProperty("codes").GetProperty("/name")[0].GetString());
    }

    [Fact]
    public async Task Validation_problem_is_in_the_endpoint_metadata()
    {
        await using TestApp app = await PeopleAppAsync();

        Endpoint endpoint = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.Single();

        Assert.Contains(endpoint.Metadata.OfType<Microsoft.AspNetCore.Http.Metadata.IProducesResponseTypeMetadata>(), produced =>
            produced.StatusCode == 400 && produced.Type == typeof(HttpValidationProblemDetails));
    }

    // The two paths: generated code (the lambdas above) and the runtime factory (a handler typed as Delegate).
    [Fact]
    public async Task Handlers_above_really_compile_through_the_generator()
    {
        await using TestApp app = await PeopleAppAsync();

        Endpoint endpoint = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.Single();

        Assert.Contains(endpoint.Metadata, IsGenerated);
    }

    [Fact]
    public async Task Runtime_factory_path_behaves_the_same()
    {
        Delegate handler = (Person person) => new Reply($"{person.Name}:{person.Age}");
        await using TestApp app = await TestApp.StartAsync(a => a.MapPost("/people", handler).ValidateBody(People));

        Endpoint endpoint = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.Single();
        Assert.DoesNotContain(endpoint.Metadata, IsGenerated);

        HttpResponseMessage valid = await app.PostJsonAsync("/people", """{ "name": " Ada " }""");
        HttpResponseMessage invalid = await app.PostJsonAsync("/people", """{ "name": "Ada", "age": "old" }""");
        HttpResponseMessage malformed = await app.PostJsonAsync("/people", "{");

        Assert.Equal("""{"text":"Ada:18"}""", await valid.Content.ReadAsStringAsync());
        await ProblemAsync(invalid, HttpStatusCode.BadRequest);
        await ProblemAsync(malformed, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Runtime_factory_path_supports_Validated()
    {
        Delegate handler = (Validated<Person> person) => person.Value.Name;
        await using TestApp app = await TestApp.StartAsync(a => a.MapPost("/people", handler).ValidateBody(People));

        HttpResponseMessage response = await app.PostJsonAsync("/people", """{ "name": " Ada " }""");

        Assert.Equal("Ada", await response.Content.ReadAsStringAsync());
    }

    private static bool IsGenerated(object metadata) =>
        metadata.GetType().Namespace == "Microsoft.AspNetCore.Http.Generated"
        || metadata is System.CodeDom.Compiler.GeneratedCodeAttribute;

    private sealed class ThrowingStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, System.Threading.CancellationToken cancellationToken = default) =>
            throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, System.Threading.CancellationToken cancellationToken) =>
            throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);
    }
}
