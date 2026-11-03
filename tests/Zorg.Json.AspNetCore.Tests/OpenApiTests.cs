using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zorg.Json.AspNetCore.OpenApi;

namespace Zorg.Json.AspNetCore.Tests;

public sealed class OpenApiTests
{
    private static readonly ObjectSchema<Person> People = PersonSchema.Create();

    private static async Task<JsonElement> DocumentAsync(Action<WebApplication> map)
    {
        await using TestApp app = await TestApp.StartAsync(
            a =>
            {
                a.MapOpenApi();
                map(a);
            },
            services => services.AddOpenApi(options => options.AddEndpointSchemas()));

        return JsonElement.Parse(await app.Client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative)));
    }

    private static JsonElement RequestSchema(JsonElement document, string path) => document
        .GetProperty("paths").GetProperty(path).GetProperty("post")
        .GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema");

    private static JsonElement ResponseSchema(JsonElement document, string path, string method, string status) => document
        .GetProperty("paths").GetProperty(path).GetProperty(method)
        .GetProperty("responses").GetProperty(status).GetProperty("content").GetProperty("application/json").GetProperty("schema");

    [Fact]
    public async Task Request_body_uses_the_input_schema()
    {
        JsonElement document = await DocumentAsync(a => a.MapPost("/people", (Person person) => person.Name).ValidateBody(People));

        JsonElement schema = RequestSchema(document, "/people");

        // Age has a default, so input may leave it out. And Trim runs first, so minLength can't be stated on input.
        Assert.Equal("name", Assert.Single(schema.GetProperty("required").EnumerateArray()).GetString());
        Assert.False(schema.GetProperty("properties").GetProperty("name").TryGetProperty("minLength", out _));
        Assert.Equal(0, schema.GetProperty("properties").GetProperty("age").GetProperty("minimum").GetInt32());
        Assert.False(schema.TryGetProperty("$schema", out _));
    }

    [Fact]
    public async Task Response_uses_the_output_schema()
    {
        JsonElement document = await DocumentAsync(a => a.MapGet("/people/{id}", (int id) => new Person("Ada", id)).ProducesSchema(People));

        JsonElement schema = ResponseSchema(document, "/people/{id}", "get", "200");

        // Output always has the defaulted property, and the constraint Trim hid from input is back.
        Assert.Equal(2, schema.GetProperty("required").GetArrayLength());
        Assert.Equal(1, schema.GetProperty("properties").GetProperty("name").GetProperty("minLength").GetInt32());
    }

    [Fact]
    public async Task Validated_parameter_is_documented_as_a_body_and_not_a_parameter()
    {
        JsonElement document = await DocumentAsync(a => a.MapPost("/people", (Validated<Person> person) => person.Value.Name).ValidateBody(People));

        Assert.Equal("object", RequestSchema(document, "/people").GetProperty("type").GetString());
        Assert.False(document.GetProperty("paths").GetProperty("/people").GetProperty("post").TryGetProperty("parameters", out _));
    }

    [Fact]
    public async Task Validation_failure_is_documented()
    {
        JsonElement document = await DocumentAsync(a => a.MapPost("/people", (Person person) => person.Name).ValidateBody(People));

        JsonElement responses = document.GetProperty("paths").GetProperty("/people").GetProperty("post").GetProperty("responses");

        Assert.True(responses.TryGetProperty("400", out _));
    }

    [Fact]
    public async Task Named_schemas_become_components_per_direction()
    {
        ObjectSchema contact = Z.Object().Property("email", Z.String().Trim().Email().Named("Email"));

        JsonElement document = await DocumentAsync(a => a
            .MapPost("/contacts", (JsonElement contact) => contact)
            .ValidateBody(contact)
            .ProducesSchema(contact));

        JsonElement schemas = document.GetProperty("components").GetProperty("schemas");

        Assert.Equal("#/components/schemas/Email", RequestSchema(document, "/contacts").GetProperty("properties").GetProperty("email").GetProperty("$ref").GetString());
        Assert.Equal("#/components/schemas/Output.Email", ResponseSchema(document, "/contacts", "post", "200").GetProperty("properties").GetProperty("email").GetProperty("$ref").GetString());
        Assert.False(schemas.GetProperty("Email").TryGetProperty("format", out _));
        Assert.Equal("email", schemas.GetProperty("Output.Email").GetProperty("format").GetString());
    }

    [Fact]
    public async Task Keywords_survive_the_conversion()
    {
        ObjectSchema schema = Z.Object()
            .Property("kind", Z.String().Const("a"))
            .Property("size", Z.Integer().ExclusiveMinimum(0).MultipleOf(2))
            .Property("tags", Z.Array(Z.String()).MinItems(1).UniqueItems())
            .Property("nick", Z.String().Optional())
            .PatternProperty("^x-", Z.String());

        JsonElement document = await DocumentAsync(a => a.MapPost("/things", ([FromBody] JsonElement body) => body).ValidateBody(schema));

        JsonElement written = RequestSchema(document, "/things");
        JsonElement properties = written.GetProperty("properties");

        Assert.Equal("a", properties.GetProperty("kind").GetProperty("const").GetString());
        Assert.Equal(0, properties.GetProperty("size").GetProperty("exclusiveMinimum").GetInt32());
        Assert.Equal(2, properties.GetProperty("size").GetProperty("multipleOf").GetInt32());
        Assert.True(properties.GetProperty("tags").GetProperty("uniqueItems").GetBoolean());
        Assert.Equal("string", written.GetProperty("patternProperties").GetProperty("^x-").GetProperty("type").GetString());
        Assert.False(written.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public async Task Other_endpoints_are_untouched()
    {
        JsonElement document = await DocumentAsync(a =>
        {
            a.MapPost("/people", (Person person) => person.Name).ValidateBody(People);
            a.MapPost("/plain", (Reply reply) => reply.Text);
        });

        Assert.Equal("#/components/schemas/Reply", RequestSchema(document, "/plain").GetProperty("$ref").GetString());
    }
}
