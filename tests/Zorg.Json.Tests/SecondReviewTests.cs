using System.Linq;
using Xunit;
using Zorg.Json.Sampling;

namespace Zorg.Json.Tests;

/// <summary>Regressions from the second code review that need no independent validator. The emission cases are in the compliance tests.</summary>
[Collection(AllocationSensitive.Name)]
public sealed class SecondReviewTests
{
    private static JsonValueSchema Import(string document) => Z.FromJsonSchema(document);

    private static void AssertLimit<T>(Schema<T> schema, string document)
    {
        ParseResult<T> result = schema.SafeParse(document);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Issues, issue => issue.Code == IssueCodes.InvalidValue);
    }

    // 7. A number whose exponent is too long to hold can't be compared, so any parse that needs to fails.

    [Theory]
    [InlineData("[1e999999999999999999, 0.1e1000000000000000000]")]
    [InlineData("[0.1e1000000000000000000, 1e999999999999999999]")]
    [InlineData("[1e10000000000000000000, 10e9999999999999999999]")]
    [InlineData("[1e10000000000000000000, 2e10000000000000000000]")]
    [InlineData("[{\"a\":1e10000000000000000000}, {\"a\":1}]")]
    public void Unique_items_fails_when_it_would_have_to_compare_a_number_it_cannot_hold(string document) =>
        AssertLimit(Import("""{"uniqueItems":true}"""), document);

    [Fact]
    public void Unique_items_still_passes_when_nothing_is_compared()
    {
        Assert.True(Import("""{"uniqueItems":true}""").SafeParse("[1e10000000000000000000]").IsSuccess);
        Assert.True(Import("""{"uniqueItems":true}""").SafeParse("[1,2,3]").IsSuccess);
    }

    [Fact]
    public void A_large_enum_fails_for_a_number_it_cannot_hold()
    {
        string values = string.Join(',', Enumerable.Range(0, 50));

        AssertLimit(Import("{\"not\":{\"enum\":[" + values + "]}}"), "1e10000000000000000000");
        AssertLimit(Import("{\"enum\":[" + values + "]}"), "1e10000000000000000000");
        AssertLimit(Import("{\"not\":{\"enum\":[1,2]}}"), "1e10000000000000000000");
        AssertLimit(Import("{\"not\":{\"const\":1}}"), "1e10000000000000000000");
    }

    [Fact]
    public void A_large_enum_that_holds_such_a_number_compares_it()
    {
        string values = string.Join(',', Enumerable.Range(0, 50));

        AssertLimit(Import("{\"not\":{\"enum\":[" + values + ",10e999999999999999999]}}"), "1e1000000000000000000");
        Assert.True(Import("{\"enum\":[" + values + ",\"a\"]}").SafeParse("\"a\"").IsSuccess);
        Assert.True(Import("{\"enum\":[" + values + "]}").SafeParse("7").IsSuccess);
    }

    [Fact]
    public void A_discriminated_union_fails_for_a_number_it_cannot_hold()
    {
        JsonValueSchema discriminated = Z.DiscriminatedUnion(
            "k",
            Z.Object().Property("k", Import("""{"const":1}""")),
            Z.Object().Property("k", Import("""{"const":2}""")));

        AssertLimit(discriminated, """{"k":1e10000000000000000000}""");
        AssertLimit(Z.Not(discriminated), """{"k":1e10000000000000000000}""");
        Assert.True(discriminated.SafeParse("""{"k":1.0}""").IsSuccess);
        Assert.True(Z.Not(discriminated).SafeParse("""{"k":3}""").IsSuccess);
    }

    // 8. A multipleOf with a huge exponent costs a modular power, and the budget has to say so.

    [Fact]
    public void A_long_multiple_of_under_not_runs_out_of_budget_instead_of_running_for_seconds()
    {
        string divisor = "1" + new string('7', 1000);
        JsonValueSchema schema = Import("{\"not\":{\"multipleOf\":" + divisor + "}}");
        string document = "[" + string.Join(',', Enumerable.Repeat("5e999999999999999999", 5_000)) + "]";
        var array = Z.Array(schema);

        // Asserting behaviour: the whole document is refused with the limit, which it was not when each number cost 56 steps.
        Assert.Contains(array.SafeParse(document).Issues, issue => issue.Code == IssueCodes.InvalidValue);
    }

    [Fact]
    public void A_short_multiple_of_with_a_huge_exponent_is_still_cheap()
    {
        var schema = Z.Array(Import("""{"multipleOf":3}"""));
        string document = "[" + string.Join(',', Enumerable.Repeat("3e999999999999999999", 2_000)) + "]";

        Assert.True(schema.SafeParse(document).IsSuccess);
    }

    // 9. Sampling a very deep schema must not end the process.

    [Fact]
    public void Sampling_a_very_deep_any_of_chain_does_not_end_the_process()
    {
        ISchema schema = Z.String();

        for (int index = 0; index < 100_000; index++)
        {
            schema = Z.AnyOf(schema);
        }

        SampleResult result = schema.GenerateSample();

        Assert.Equal(result.IsValid, result.Issues.IsEmpty);
    }

    [Fact]
    public void Sampling_a_deep_any_of_chain_within_reach_still_finds_a_value()
    {
        ISchema schema = Z.String().MinLength(2);

        for (int index = 0; index < 500; index++)
        {
            schema = Z.AnyOf(schema);
        }

        SampleResult result = schema.GenerateSample();

        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(issue => issue.Message)));
        Assert.Equal(System.Text.Json.JsonValueKind.String, result.Value.ValueKind);
    }
}
