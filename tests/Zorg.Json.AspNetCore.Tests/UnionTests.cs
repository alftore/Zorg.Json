#if NET11_0_OR_GREATER
using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zorg.Json.AspNetCore.OpenApi;

namespace Zorg.Json.AspNetCore.Tests;

public sealed record CardPayment(string Card, decimal Amount);

public sealed record BankPayment(string Iban, decimal Amount, string? Reference);

// A C# union compiles to a struct. These are test data, compared through their JSON, so CA1815 doesn't apply.
#pragma warning disable CA1815
[JsonUnion(TypeClassifier = typeof(JsonUnionTypeStructuralClassifier))]
public union Payment(CardPayment, BankPayment);
#pragma warning restore CA1815

public sealed record Checkout(string Order, Payment Payment);

[JsonSerializable(typeof(Checkout))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
internal sealed partial class UnionJson : JsonSerializerContext;

/// <summary>A C# union in a request body: ValidateBody, the serializer's binding and the OpenAPI 3.2 document of ASP.NET Core 11.</summary>
public sealed class UnionTests
{
    private static readonly ObjectSchema<Checkout> Checkouts = Schema.Object(UnionJson.Default.Checkout)
        .Property(c => c.Order, s => s.MinLength(1));

    private static Task<TestApp> StartAsync() => TestApp.StartAsync(
        app =>
        {
            app.MapOpenApi();
            app.MapPost("/checkout", (Checkout checkout) => checkout.Payment.Value switch
            {
                CardPayment card => $"card {card.Card} {card.Amount}",
                BankPayment bank => $"bank {bank.Iban} {bank.Reference}",
                _ => "none",
            }).ValidateBody(Checkouts);
        },
        services =>
        {
            services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, UnionJson.Default));
            services.AddOpenApi(options => options.AddEndpointSchemas());
        });

    [Theory]
    [InlineData("""{"order":"a1","payment":{"card":"4242","amount":10}}""", "card 4242 10")]
    [InlineData("""{"order":"a1","payment":{"iban":"NO93","amount":10,"reference":"r"}}""", "bank NO93 r")]
    public async Task A_union_body_reaches_the_handler_as_the_case_the_serializer_picks(string json, string expected)
    {
        await using TestApp app = await StartAsync();

        using HttpResponseMessage response = await app.PostJsonAsync("/checkout", json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("""{"order":"","payment":{"card":"4242","amount":10}}""")]
    [InlineData("""{"order":"a1","payment":42}""")]
    public async Task A_union_body_that_breaks_a_rule_is_refused(string json)
    {
        await using TestApp app = await StartAsync();

        using HttpResponseMessage response = await app.PostJsonAsync("/checkout", json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_OpenAPI_document_states_the_union_as_its_cases_and_null()
    {
        await using TestApp app = await StartAsync();

        JsonElement document = JsonElement.Parse(await app.Client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative)));
        JsonElement payment = document.GetProperty("paths").GetProperty("/checkout").GetProperty("post").GetProperty("requestBody")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("properties").GetProperty("payment");

        Assert.StartsWith("3.2", document.GetProperty("openapi").GetString(), StringComparison.Ordinal);
        Assert.Equal(3, payment.GetProperty("anyOf").GetArrayLength());
    }
}
#endif
