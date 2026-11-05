using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Zorg.Json.Tests;

/// <summary>Regressions from a second code review.</summary>
public sealed class ReviewFollowUpTests
{
    private static JsonValueSchema J(string schema) => Z.FromJsonSchema(schema);

    private static JsonDocument Bytes(params object[] parts)
    {
        List<byte> bytes = [];

        foreach (object part in parts)
        {
            if (part is byte single)
            {
                bytes.Add(single);
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes((string)part));
            }
        }

        return JsonDocument.Parse(bytes.ToArray());
    }

    [Fact]
    public void Instance_paths_in_speculative_branches_are_bounded()
    {
        const int Length = 40_000;
        string json = "{\"" + new string('k', Length) + "\":[" + string.Join(',', Enumerable.Repeat("0", Length)) + "]}";
        JsonElement element = JsonDocument.Parse(json).RootElement;
        Schema<JsonElement>[] schemas = [Z.Record(Z.Array(Z.Not(Z.String()))), Z.Record(Z.Array().Contains(Z.String(), 0))];

        foreach (Schema<JsonElement> schema in schemas)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            ParseResult<JsonElement> result = schema.SafeParse(element);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(allocated < 400_000_000, $"{allocated / 1_000_000} MB");
            Assert.True(result.IsSuccess);
        }
    }

    private static ObjectSchema Branch(string value) => Z.Object().Property("k", J($$"""{"const":{{value}}}"""));

    [Fact]
    public void An_unreadable_string_fails_the_parse_under_a_discriminated_union()
    {
        JsonValueSchema union = Z.DiscriminatedUnion("k", Branch("\"a\""), Branch("\"b\""));
        const string Input = """{"k":"\ud800"}""";

        Assert.False(union.SafeParse(Input).IsSuccess);
        Assert.False(Z.Not(union).SafeParse(Input).IsSuccess);
        Assert.False(Z.If(union, Z.Never(), Z.Any()).SafeParse(Input).IsSuccess);
        Assert.False(Z.AnyOf(union, Z.Any()).SafeParse(Input).IsSuccess);
        Assert.False(Z.Not(Z.OneOf(Branch("\"a\""), Branch("\"b\""))).SafeParse(Input).IsSuccess);
    }

    [Fact]
    public void An_unreadable_string_fails_the_parse_under_const_and_enum()
    {
        Assert.False(Z.Not(J("""{"const":"a"}""")).SafeParse("\"\\ud800\"").IsSuccess);
        Assert.False(Z.Not(J("""{"enum":["a","b"]}""")).SafeParse("\"\\ud800\"").IsSuccess);
        Assert.False(Z.Not(J("""{"const":{"x":"a"}}""")).SafeParse("""{"x":"\ud800"}""").IsSuccess);
        Assert.True(Z.Not(J("""{"const":1}""")).SafeParse("\"\\ud800\"").IsSuccess);
        Assert.True(Z.Not(J("""{"const":"a"}""")).SafeParse("\"b\"").IsSuccess);
    }

    [Fact]
    public void A_discriminator_compares_json_values_not_text()
    {
        JsonValueSchema numbers = Z.DiscriminatedUnion("k", Branch("1"), Branch("2"));

        Assert.True(numbers.SafeParse("""{"k":1.0}""").IsSuccess);
        Assert.True(numbers.SafeParse("""{"k":2e0}""").IsSuccess);
        Assert.False(numbers.SafeParse("""{"k":3}""").IsSuccess);

        JsonValueSchema objects = Z.DiscriminatedUnion("k", Branch("""{"a":1,"b":2}"""), Branch("""{"a":1}"""));

        Assert.True(objects.SafeParse("""{"k":{"b":2,"a":1}}""").IsSuccess);
        Assert.Equal(IssueCodes.InvalidUnion, objects.SafeParse("""{"k":{"a":2}}""").Issues[0].Code);
    }

    [Fact]
    public void Branches_with_equal_discriminator_values_are_a_plain_one_of()
    {
        JsonValueSchema union = Z.DiscriminatedUnion("k", Branch("1"), Branch("1.0"));

        Assert.False(union.SafeParse("""{"k":1}""").IsSuccess);
    }

    [Fact]
    public void Invalid_utf8_inside_an_element_does_not_throw()
    {
        ObjectSchema strip = Z.Object().Strip().Property("a", Z.Any());
        ObjectSchema fill = Z.Object().Passthrough().Property("a", Z.Any()).Property("d", Z.Integer().Default(1));
        JsonValueSchema tuple = Z.Tuple(Z.String().Trim(), Z.Any());
        JsonValueSchema union = Z.DiscriminatedUnion("k", Z.Object().Property("k", Z.String().Const("a")));

        using JsonDocument object1 = Bytes("{\"a\":\"", (byte)0xFF, "\",\"b\":1}");
        using JsonDocument array = Bytes("[\" x \",\"", (byte)0xFF, "\"]");
        using JsonDocument keyed = Bytes("{\"k\":[\"", (byte)0xFF, "\"]}");

        Assert.True(strip.SafeParse(object1.RootElement).IsSuccess);
        Assert.True(fill.SafeParse(object1.RootElement).IsSuccess);
        Assert.True(tuple.SafeParse(array.RootElement).IsSuccess);
        Assert.False(union.SafeParse(keyed.RootElement).IsSuccess);
    }
}
