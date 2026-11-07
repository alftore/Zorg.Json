using System;
using System.Linq;
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

    private static async Task<JsonElement> DocumentAsync(Action<WebApplication> map, Microsoft.OpenApi.OpenApiSpecVersion version = Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_1)
    {
        await using TestApp app = await TestApp.StartAsync(
            a =>
            {
                a.MapOpenApi();
                map(a);
            },
            services => services.AddOpenApi(options =>
            {
                options.OpenApiVersion = version;
                options.AddEndpointSchemas();
            }));

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

    [Theory]
    [InlineData(Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_1)]
    [InlineData(Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_0)]
    public async Task A_tuple_is_not_closed_in_the_document(Microsoft.OpenApi.OpenApiSpecVersion version)
    {
        ObjectSchema schema = Z.Object().Property("t", Z.Tuple(Z.String(), Z.Integer()));

        JsonElement document = await DocumentAsync(a => a.MapPost("/t", (JsonElement body) => "t").ValidateBody(schema), version);
        JsonElement tuple = RequestSchema(document, "/t").GetProperty("properties").GetProperty("t");

        Assert.False(tuple.TryGetProperty("items", out _));
        Assert.Equal(2, tuple.GetProperty("minItems").GetInt32());
    }

    [Theory]
    [InlineData(Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_1)]
    [InlineData(Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_0)]
    public async Task Constants_and_boolean_schemas_keep_their_JSON(Microsoft.OpenApi.OpenApiSpecVersion version)
    {
        ObjectSchema schema = Z.Object()
            .Property("v", Z.Integer().Const(3))
            .Property("b", Z.Boolean().Const(true))
            .Property("nv", Z.Never().Optional());

        JsonElement document = await DocumentAsync(a => a.MapPost("/c", (JsonElement body) => "c").ValidateBody(schema), version);
        JsonElement properties = RequestSchema(document, "/c").GetProperty("properties");

        Assert.Equal(3, Assert.Single(properties.GetProperty("v").GetProperty("enum").EnumerateArray()).GetInt32());
        Assert.True(Assert.Single(properties.GetProperty("b").GetProperty("enum").EnumerateArray()).GetBoolean());
        Assert.Equal(JsonValueKind.Object, properties.GetProperty("nv").ValueKind);
    }

    [Fact]
    public async Task A_named_schema_and_its_nullable_form_on_two_endpoints_say_why_they_clash()
    {
        ObjectSchema named = Z.Object().Property("q", Z.String()).Named("N");

        Exception exception = await Assert.ThrowsAnyAsync<Exception>(() => DocumentAsync(a =>
        {
            a.MapPost("/a", (JsonElement body) => "a").ValidateBody(Z.Object().Property("p", named));
            a.MapPost("/b", (JsonElement body) => "b").ValidateBody(Z.Object().Property("p", named.Nullable()));
        }));

        Assert.Contains("such as Nullable()", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_name_the_framework_already_gave_a_type_is_refused()
    {
        ObjectSchema named = Z.Object().Property("name", Z.String()).Named("Person");

        Exception exception = await Assert.ThrowsAnyAsync<Exception>(() => DocumentAsync(a =>
        {
            a.MapGet("/p", () => new Person("a", 1));
            a.MapPost("/t", (JsonElement body) => "t").ValidateBody(Z.Object().Property("lead", named));
        }));

        Assert.Contains("already has a component named 'Person'", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_number_past_double_does_not_break_the_document()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""{"type":"object","properties":{"n":{"const":1e400},"m":{"enum":[1e400,1]},"d":{"default":1e400}}}""");

        JsonElement document = await DocumentAsync(a => a.MapPost("/h", (JsonElement body) => "h").ValidateBody(schema));

        Assert.False(RequestSchema(document, "/h").GetProperty("properties").GetProperty("n").TryGetProperty("enum", out _));
    }

    [Fact]
    public async Task Pattern_properties_do_not_close_a_3_0_document()
    {
        ObjectSchema schema = Z.Object().Strict().PatternProperty("^x-", Z.String());

        JsonElement document = await DocumentAsync(a => a.MapPost("/p", (JsonElement body) => "p").ValidateBody(schema), Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_0);

        Assert.False(RequestSchema(document, "/p").TryGetProperty("additionalProperties", out _));
    }

    [Fact]
    public async Task Recursive_imports_on_two_endpoints_share_one_document()
    {
        JsonValueSchema first = Z.FromJsonSchema("""{"type":"object","properties":{"kid":{"$ref":"#"},"a":{"type":"string"}}}""");
        JsonValueSchema second = Z.FromJsonSchema("""{"type":"object","properties":{"kid":{"$ref":"#"},"b":{"type":"integer"}}}""");

        JsonElement document = await DocumentAsync(a =>
        {
            a.MapPost("/first", (JsonElement body) => "a").ValidateBody(first);
            a.MapPost("/second", (JsonElement body) => "b").ValidateBody(second);
        });

        Assert.Equal(2, document.GetProperty("components").GetProperty("schemas").EnumerateObject().Count(entry => entry.Name.StartsWith("target-", StringComparison.Ordinal)));
    }

    private const string WithDefinition = """{"type":"object","definitions":{"a":{"type":"string"}},"properties":{"p":{"$ref":"#/definitions/a"}}}""";

    private const string WithUnknownText = """{"type":"object","x-u":{"a":{"type":"string"}},"properties":{"p":{"$ref":"#/x-u/a"}}}""";

    /// <summary>The components of a document with two endpoints, name and text, sorted. Everything else in it is the framework's.</summary>
    private static async Task<string[]> ComponentsAsync(ISchema first, ISchema second)
    {
        static RouteHandlerBuilder Endpoint(WebApplication app, string path, ISchema schema)
        {
            RouteHandlerBuilder route = app.MapPost(path, (JsonElement body) => "ok");
            return schema is ObjectSchema closed ? route.ValidateBody(closed) : route.ValidateBody((JsonValueSchema)schema);
        }

        JsonElement document = await DocumentAsync(a =>
        {
            Endpoint(a, "/first", first);
            Endpoint(a, "/second", second);
        });

        return [.. document.GetProperty("components").GetProperty("schemas").EnumerateObject()
            .Where(entry => entry.Name is not "ProblemDetails" and not "HttpValidationProblemDetails" and not "JsonElement")
            .Select(entry => $"{entry.Name} {entry.Value.GetRawText()}")
            .Order(StringComparer.Ordinal)];
    }

    [Theory]
    [InlineData(WithDefinition)]
    [InlineData(WithUnknownText)]
    public async Task A_named_import_in_two_wrappers_is_one_component(string text)
    {
        JsonValueSchema named = Z.FromJsonSchema(text).Named("X");

        string[] components = await ComponentsAsync(Z.Object().Property("one", named), Z.Object().Property("two", named));

        Assert.Equal(2, components.Length);
        Assert.Single(components, component => component.StartsWith("X ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_named_import_as_a_body_and_in_an_array_leaves_no_orphan()
    {
        JsonValueSchema named = Z.FromJsonSchema(WithDefinition).Named("X");

        string[] components = await ComponentsAsync(named, Z.AnyOf(Z.Array(named), Z.Null()));

        Assert.Equal(2, components.Length);
    }

    [Theory]
    [InlineData("""{"$defs":{"a":{"type":"TYPE"}},"properties":{"p":{"$ref":"#/$defs/a"}}}""")]
    [InlineData("""{"$schema":"http://json-schema.org/draft-07/schema#","definitions":{"a":{"type":"TYPE"}},"properties":{"p":{"$ref":"#/definitions/a"}}}""")]
    [InlineData("""{"$defs":{"a":{"$ref":"#/$defs/b"},"b":{"type":"TYPE"}},"properties":{"p":{"$ref":"#/$defs/a"}}}""")]
    [InlineData("""{"$defs":{"a":{"properties":{"n":{"$ref":"#/$defs/b"},"k":{"type":"TYPE"}}},"b":{"properties":{"n":{"$ref":"#/$defs/a"}}}},"properties":{"p":{"$ref":"#/$defs/a"}}}""")]
    public async Task Definitions_of_one_name_in_two_documents_do_not_clash_in_either_order(string template)
    {
        JsonValueSchema text = Z.FromJsonSchema(template.Replace("TYPE", "string", StringComparison.Ordinal));
        JsonValueSchema number = Z.FromJsonSchema(template.Replace("TYPE", "integer", StringComparison.Ordinal));

        string[] one = await ComponentsAsync(text, number);
        string[] other = await ComponentsAsync(number, text);

        Assert.Equal(one, other);
        Assert.Equal(one.Length, one.Select(component => component[..component.IndexOf(' ', StringComparison.Ordinal)]).Distinct().Count());
    }

    [Fact]
    public async Task The_same_definitions_in_two_documents_are_one_component()
    {
        const string Text = """{"$defs":{"a":{"type":"string"}},"properties":{"p":{"$ref":"#/$defs/a"}}}""";
        const string Other = """{"$defs":{"a":{"type":"string"}},"properties":{"q":{"$ref":"#/$defs/a"}}}""";

        string[] components = await ComponentsAsync(Z.FromJsonSchema(Text), Z.FromJsonSchema(Other));

        Assert.Single(components);
    }

    private const string Tree = """{"$defs":{"a":{"type":"object","properties":{"up":{"$ref":"#"}}}},"type":"object","properties":{"kid":{"$ref":"#/$defs/a"}}}""";

    [Fact]
    public async Task One_import_named_twice_on_two_endpoints_keeps_its_own_definitions()
    {
        string[] components = await ComponentsAsync(
            Z.Object().Property("x", Z.FromJsonSchema(Tree).Named("A")),
            Z.Object().Property("y", Z.FromJsonSchema(Tree).Named("B")));

        Assert.Equal(4, components.Length);
    }

    [Fact]
    public async Task One_import_in_two_nested_names_keeps_its_own_definitions()
    {
        JsonValueSchema imported = Z.FromJsonSchema(Tree);

        string[] components = await ComponentsAsync(
            Z.Object().Property("x", Z.Object().Property("v", imported.Named("A")).Named("Outer1")),
            Z.Object().Property("y", Z.Object().Property("v", imported.Named("B")).Named("Outer2")));

        Assert.Equal(6, components.Length);
    }

    [Theory]
    [InlineData("""{"$ref":"#/$defs/a","description":"d","$defs":{"a":{"type":"string"}}}""")]
    [InlineData("""{"$defs":{"a":{"type":"string"},"b":{"$ref":"#/$defs/a","minLength":3}},"type":"object","properties":{"p":{"$ref":"#/$defs/b"}}}""")]
    public async Task A_reference_with_siblings_at_the_top_of_a_schema_can_be_read(string text)
    {
        JsonValueSchema schema = Z.FromJsonSchema(text);

        string[] components = await ComponentsAsync(schema, schema);

        Assert.NotEmpty(components);
    }

    [Fact]
    public async Task Two_different_schemas_named_alike_still_clash()
    {
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => ComponentsAsync(
            Z.Object().Property("v", Z.String().Named("Same")),
            Z.Object().Property("v", Z.Number<int>().Named("Same"))));

        Assert.Contains("Same", error.Message, StringComparison.Ordinal);
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
    public async Task Different_schemas_with_one_name_fail_clearly()
    {
        ObjectSchema first = Z.Object().Property("v", Z.String().Email().Named("x"));
        ObjectSchema second = Z.Object().Property("v", Z.String().Uuid().Named("x"));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => DocumentAsync(a =>
        {
            a.MapPost("/a", (JsonElement body) => body).ValidateBody(first);
            a.MapPost("/b", (JsonElement body) => body).ValidateBody(second);
        }));

        Assert.Contains("'x'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Identical_schemas_with_one_name_share_a_component()
    {
        ObjectSchema first = Z.Object().Property("v", Z.String().Email().Named("x"));
        ObjectSchema second = Z.Object().Property("w", Z.String().Email().Named("x"));

        JsonElement document = await DocumentAsync(a =>
        {
            a.MapPost("/a", (JsonElement body) => body).ValidateBody(first);
            a.MapPost("/b", (JsonElement body) => body).ValidateBody(second);
        });

        Assert.Equal("email", document.GetProperty("components").GetProperty("schemas").GetProperty("x").GetProperty("format").GetString());
    }

    [Fact]
    public async Task A_top_reference_keeps_the_indexes_of_its_all_of()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""
            {"$defs":{"b":{"maxLength":9},"a":{"$ref":"#/$defs/b","allOf":[{"type":"string"},{"minLength":2}]}},
             "type":"object","properties":{"p":{"$ref":"#/$defs/a"},"q":{"$ref":"#/$defs/a/allOf/0"}}}
            """);

        JsonElement document = await DocumentAsync(a => a.MapPost("/things", ([FromBody] JsonElement body) => body).ValidateBody(schema));
        JsonElement schemas = document.GetProperty("components").GetProperty("schemas");
        JsonElement holder = schemas.EnumerateObject().Single(entry => entry.Name.StartsWith("a-", StringComparison.Ordinal)).Value;

        Assert.Equal("string", holder.GetProperty("allOf")[0].GetProperty("type").GetString());
        Assert.Equal(2, holder.GetProperty("allOf")[1].GetProperty("minLength").GetInt32());
    }

    [Theory]
    [InlineData("""{"type":"object","properties":{"p":true}}""")]
    [InlineData("""{"type":"object","properties":{"p":false}}""")]
    [InlineData("""{"type":"array","items":true}""")]
    [InlineData("""{"type":"object","additionalProperties":true}""")]
    [InlineData("""{"type":"object","additionalProperties":false}""")]
    [InlineData("""{"allOf":[true,{"type":"object"}]}""")]
    [InlineData("""{"not":true}""")]
    [InlineData("""{"$defs":{"a":true},"properties":{"p":{"$ref":"#/$defs/a"}}}""")]
    public async Task Boolean_subschemas_do_not_break_the_document(string text)
    {
        JsonValueSchema schema = Z.FromJsonSchema(text);

        JsonElement document = await DocumentAsync(a => a.MapPost("/things", ([FromBody] JsonElement body) => body).ValidateBody(schema));

        Assert.Equal(JsonValueKind.Object, document.GetProperty("paths").ValueKind);
    }

    [Fact]
    public async Task A_nested_reference_keeps_what_sits_beside_it()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""
            {"$defs":{"a":{"type":"string"}},"type":"object","properties":{"p":{"$ref":"#/$defs/a","minLength":2,"description":"beside"}}}
            """);

        JsonElement document = await DocumentAsync(a => a.MapPost("/things", ([FromBody] JsonElement body) => body).ValidateBody(schema));
        string text = document.GetRawText();

        Assert.Contains("\"minLength\": 2", text.Replace("\"minLength\":2", "\"minLength\": 2", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_schema_on_two_endpoints_repeats_no_id_or_anchor()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""
            {"$id":"http://e.com/s","$defs":{"a":{"$anchor":"n","type":"string"}},"type":"object","properties":{"p":{"$ref":"#n"}}}
            """);

        JsonElement document = await DocumentAsync(a =>
        {
            a.MapPost("/one", ([FromBody] JsonElement body) => body).ValidateBody(schema);
            a.MapPost("/two", ([FromBody] JsonElement body) => body).ValidateBody(schema);
        });

        string text = document.GetRawText();
        Assert.DoesNotContain("\"$id\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"$anchor\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Definitions_that_differ_in_the_spelling_of_a_reference_do_not_clash()
    {
        JsonValueSchema first = Z.FromJsonSchema("""{"$defs":{"t":{"$anchor":"z","type":"string"},"u":{"properties":{"p":{"$ref":"#z"}}}},"properties":{"q":{"$ref":"#/$defs/u"}}}""");
        JsonValueSchema second = Z.FromJsonSchema("""{"$defs":{"t":{"$anchor":"z","type":"string"},"u":{"properties":{"p":{"$ref":"#/$defs/t"}}}},"properties":{"q":{"$ref":"#/$defs/u"}}}""");

        JsonElement document = await DocumentAsync(a =>
        {
            a.MapPost("/one", ([FromBody] JsonElement body) => body).ValidateBody(first);
            a.MapPost("/two", ([FromBody] JsonElement body) => body).ValidateBody(second);
        });

        Assert.Equal(JsonValueKind.Object, document.GetProperty("components").ValueKind);
    }

    [Fact]
    public async Task A_cycle_across_both_definition_keywords_shares_one_set_of_names()
    {
        JsonValueSchema first = Z.FromJsonSchema("""{"$defs":{"a":{"properties":{"b":{"$ref":"#/definitions/b"}}}},"definitions":{"b":{"properties":{"a":{"$ref":"#/$defs/a"}}}},"properties":{"p":{"$ref":"#/$defs/a"}}}""");
        JsonValueSchema second = Z.FromJsonSchema("""{"$defs":{"a":{"properties":{"b":{"$ref":"#/definitions/b"}}}},"definitions":{"b":{"properties":{"a":{"$ref":"#/$defs/a"}}}},"properties":{"p":{"$ref":"#/definitions/b"}}}""");

        JsonElement document = await DocumentAsync(a =>
        {
            a.MapPost("/one", ([FromBody] JsonElement body) => body).ValidateBody(first);
            a.MapPost("/two", ([FromBody] JsonElement body) => body).ValidateBody(second);
        });

        string[] names = [.. document.GetProperty("components").GetProperty("schemas").EnumerateObject().Select(entry => entry.Name)];
        Assert.Equal(2, names.Count(name => name.StartsWith("a-", StringComparison.Ordinal) || name.StartsWith("b-", StringComparison.Ordinal) || name.StartsWith("target-", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Keywords_survive_the_conversion()
    {
        ObjectSchema schema = Z.Object().Strict()
            .Property("kind", Z.String().Const("a"))
            .Property("size", Z.Integer().ExclusiveMinimum(0).MultipleOf(2))
            .Property("tags", Z.Array(Z.String()).MinItems(1).UniqueItems())
            .Property("nick", Z.String().Optional())
            .PatternProperty("^x-", Z.String());

        JsonElement document = await DocumentAsync(a => a.MapPost("/things", ([FromBody] JsonElement body) => body).ValidateBody(schema));

        JsonElement written = RequestSchema(document, "/things");
        JsonElement properties = written.GetProperty("properties");

        Assert.Equal("a", Assert.Single(properties.GetProperty("kind").GetProperty("enum").EnumerateArray()).GetString());
        Assert.Equal(0, properties.GetProperty("size").GetProperty("exclusiveMinimum").GetInt32());
        Assert.Equal(2, properties.GetProperty("size").GetProperty("multipleOf").GetInt32());
        Assert.True(properties.GetProperty("tags").GetProperty("uniqueItems").GetBoolean());
        Assert.Equal("string", written.GetProperty("patternProperties").GetProperty("^x-").GetProperty("type").GetString());
        Assert.False(written.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public async Task Definitions_of_an_imported_schema_become_components()
    {
        JsonValueSchema imported = Z.FromJsonSchema("""{"$defs":{"item":{"type":"string"}},"type":"array","items":{"$ref":"#/$defs/item"}}""");

        JsonElement document = await DocumentAsync(a => a.MapPost("/items", ([FromBody] JsonElement body) => body).ValidateBody(imported));

        JsonElement written = RequestSchema(document, "/items");

        Assert.False(written.TryGetProperty("$defs", out _));
        string reference = written.GetProperty("items").GetProperty("$ref").GetString()!;

        Assert.StartsWith("#/components/schemas/item-", reference, StringComparison.Ordinal);
        Assert.Equal("string", document.GetProperty("components").GetProperty("schemas").GetProperty(reference["#/components/schemas/".Length..]).GetProperty("type").GetString());
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
