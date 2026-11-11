using System;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Zorg.Json.Sampling;

namespace Zorg.Json.Tests;

/// <summary>Cases from a code review of <c>GenerateSample</c>.</summary>
public sealed class SamplingReviewTests
{
    private static SampleResult WithinSeconds(int seconds, Func<SampleResult> sample)
    {
        Task<SampleResult> task = Task.Run(sample);
        Assert.True(task.Wait(TimeSpan.FromSeconds(seconds), TestContext.Current.CancellationToken), $"no sample after {seconds} seconds");
        return task.GetAwaiter().GetResult();
    }

    [Theory]
    [InlineData("""{"minProperties":1,"propertyNames":{"const":"\ud800"}}""")]
    [InlineData("""{"minProperties":1,"propertyNames":{"enum":["\ud800"]}}""")]
    [InlineData("""{"minProperties":1,"propertyNames":{"enum":["\ud800","ok"]}}""")]
    public void A_property_name_that_is_not_valid_Unicode_does_not_make_sampling_throw(string document)
    {
        JsonValueSchema schema = Z.FromJsonSchema(document);

        SampleResult result = schema.GenerateSample();

        Assert.True(!result.IsValid || schema.SafeParse(result.Value).IsSuccess);
    }

    [Fact]
    public void A_name_that_is_valid_is_found_beside_one_that_is_not()
    {
        JsonValueSchema schema = Z.FromJsonSchema("""{"minProperties":1,"propertyNames":{"enum":["\ud800","ok"]}}""");

        SampleResult result = schema.GenerateSample();

        Assert.True(result.IsValid, result.Value.GetRawText());
        Assert.True(schema.SafeParse(result.Value).IsSuccess);
    }

    [Theory]
    [InlineData(2_000)]
    public void Distinct_integers_are_found_in_time(int count)
    {
        ArraySchema<long> schema = Z.Array(Z.Integer()).MinItems(count).UniqueItems();

        SampleResult result = WithinSeconds(30, () => schema.GenerateSample());

        Assert.True(result.IsValid);
        Assert.Equal(count, result.Value.GetArrayLength());
        Assert.True(schema.SafeParse(result.Value).IsSuccess);
    }

    [Theory]
    [InlineData(2_000)]
    public void Distinct_strings_are_found_in_time(int count)
    {
        ArraySchema<string> schema = Z.Array(Z.String()).MinItems(count).UniqueItems();

        SampleResult result = WithinSeconds(30, () => schema.GenerateSample());

        Assert.True(result.IsValid);
        Assert.Equal(count, result.Value.GetArrayLength());
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(10_000)]
    public void A_schema_that_rejects_every_document_ends_the_search_in_time(int count)
    {
        JsonValueSchema schema = Z.FromJsonSchema("{\"type\":\"array\",\"minItems\":" + count + ",\"uniqueItems\":true,\"items\":{\"type\":\"string\"},\"not\":{}}");

        SampleResult result = WithinSeconds(60, () => schema.GenerateSample());

        Assert.False(result.IsValid);
    }

    [Fact]
    public void A_sample_that_text_parsing_would_refuse_for_its_depth_is_not_reported_as_valid()
    {
        ISchema schema = Z.Array();

        for (int index = 0; index < 64; index++)
        {
            schema = Z.Array().MinItems(1).Contains(schema);
        }

        ArraySchema<JsonElement> nested = (ArraySchema<JsonElement>)schema;
        SampleResult result = nested.GenerateSample();

        Assert.True(!result.IsValid || nested.SafeParse(result.Value.GetRawText()).IsSuccess, "valid, but the text is refused");
    }

    [Fact]
    public void A_const_nested_beyond_the_text_limit_is_not_reported_as_valid()
    {
        string deep = new string('[', 70) + "1" + new string(']', 70);
        JsonElement document = JsonElement.Parse("{\"const\":" + deep + "}", new JsonDocumentOptions { MaxDepth = 200 });
        JsonValueSchema schema = Z.FromJsonSchema(document);

        SampleResult result = schema.GenerateSample();

        Assert.True(!result.IsValid || schema.SafeParse(result.Value.GetRawText()).IsSuccess, "valid, but the text is refused");
    }

    [Fact]
    public void A_sample_within_the_text_limit_is_still_valid()
    {
        string deep = new string('[', 60) + "1" + new string(']', 60);
        JsonValueSchema schema = Z.FromJsonSchema("{\"const\":" + deep + "}");

        SampleResult result = schema.GenerateSample();

        Assert.True(result.IsValid);
        Assert.True(schema.SafeParse(result.Value.GetRawText()).IsSuccess);
    }
}
