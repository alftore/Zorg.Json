using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Zorg.Json.AspNetCore.Tests;

public sealed class ProblemDetailsTests
{
    [Fact]
    public void Errors_and_codes_share_keys_and_order()
    {
        ParseResult<JsonElement> result = Z.Object()
            .Property("name", Z.String().MinLength(3).Pattern("^a"))
            .SafeParse("""{ "name": "b" }""");

        HttpValidationProblemDetails details = result.Issues.ToProblemDetails();

        JsonElement codes = Assert.IsType<JsonElement>(details.Extensions[ProblemDetailsExtensions.CodesExtension]);
        Assert.Equal(400, details.Status);
        Assert.Equal(2, details.Errors["/name"].Length);
        Assert.Equal(2, codes.GetProperty("/name").GetArrayLength());
    }

    [Fact]
    public void Pointers_differing_in_case_stay_apart()
    {
        ParseResult<JsonElement> result = Z.Object()
            .Property("a", Z.String())
            .Property("A", Z.String())
            .SafeParse("""{ "a": 1, "A": 2 }""");

        HttpValidationProblemDetails details = result.Issues.ToProblemDetails();

        Assert.Equal(2, details.Errors.Count);
    }

    [Fact]
    public void Nested_paths_are_json_pointers()
    {
        ParseResult<JsonElement> result = Z.Object()
            .Property("tags", Z.Array(Z.String()))
            .SafeParse("""{ "tags": ["ok", 1] }""");

        Assert.Contains("/tags/1", result.Issues.ToProblemDetails().Errors.Keys);
    }
}
