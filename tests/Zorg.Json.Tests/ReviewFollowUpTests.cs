using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Zorg.Json.Tests;

/// <summary>Regressions from a second code review.</summary>
[Collection(AllocationSensitive.Name)]
public sealed class ReviewFollowUpTests
{
    [Fact]
    public void A_very_deep_fluent_schema_does_not_end_the_process()
    {
        ISchema inner = Z.Integer();

        for (int level = 0; level < 30_000; level++)
        {
            inner = Z.Object().Property("a", inner);
        }

        Assert.False(((ObjectSchema)inner).SafeParse("{}").IsSuccess);
        Assert.False(((ObjectSchema)inner).SafeParse(string.Concat(Enumerable.Repeat("{\"a\":", 600)) + "1" + new string('}', 600)).IsSuccess);
    }

    [Fact]
    public void A_long_reference_chain_does_not_end_the_process()
    {
        const int Links = 4_900;
        string defs = string.Join(',', Enumerable.Range(0, Links).Select(n => $"\"a{n}\":{{\"properties\":{{\"x\":{{\"$ref\":\"#/$defs/a{n + 1}\"}}}}}}"));
        JsonValueSchema schema = Z.FromJsonSchema($"{{\"$ref\":\"#/$defs/a0\",\"$defs\":{{{defs},\"a{Links}\":{{}}}}}}");

        Assert.True(schema.SafeParse("{}").IsSuccess);
    }

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
    public void Decimal_constants_with_equal_values_are_a_plain_one_of()
    {
        ObjectSchema one = Z.Object().Property("k", Z.Number<decimal>().Const(1m));
        ObjectSchema other = Z.Object().Property("k", Z.Number<decimal>().Const(1.0m));
        JsonValueSchema union = Z.DiscriminatedUnion("k", one, other);

        Assert.False(union.SafeParse("""{"k":1}""").IsSuccess);
    }

    private static void Quick(Action action, double seconds = 5)
    {
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        action();
        Assert.True(watch.Elapsed.TotalSeconds < seconds, watch.Elapsed.ToString());
    }

    [Fact]
    public void Long_numbers_are_read_in_linear_time()
    {
        string digits = new('7', 8_000_000);

        Quick(() => Assert.True(Z.Number().Minimum(0).SafeParse("0." + digits).IsSuccess));
        Quick(() => Assert.False(J("""{"const":1}""").SafeParse(digits).IsSuccess));
        Quick(() => Assert.False(Z.Array().UniqueItems().SafeParse("[" + digits + "," + digits + "]").IsSuccess));
        Quick(() => Assert.True(J("""{"multipleOf":3}""").SafeParse(new string('3', 1_000_000)).IsSuccess));
    }

    [Fact]
    public void A_number_with_a_long_zero_tail_is_not_a_type_error()
    {
        // Z.Number() reads 0.7...7e-99999999 as zero in a double, so it doesn't fit, and that must be quick.
        string text = "0." + new string('7', 8_000_000) + "e-99999999";

        Quick(() => Assert.False(Z.Number().SafeParse(text).IsSuccess));
    }

    [Fact]
    public void Exponents_are_compared_exactly()
    {
        Assert.False(J("""{"const":1e2000000000}""").SafeParse("1e3000000000").IsSuccess);
        Assert.False(J("""{"const":1e1073741823}""").SafeParse("1e1073741824").IsSuccess);
        Assert.True(J("""{"const":1e2000000000}""").SafeParse("1e2000000000").IsSuccess);
        Assert.True(Z.Array().UniqueItems().SafeParse("[1e2000000000,1e3000000000]").IsSuccess);
        Assert.False(J("""{"maximum":1e2000000000}""").SafeParse("1e3000000000").IsSuccess);
        Assert.True(J("""{"const":1.5e99999999999}""").SafeParse("15e99999999998").IsSuccess);
    }

    [Fact]
    public void An_exponent_too_long_to_hold_fails_the_parse()
    {
        Assert.False(J("""{"maximum":10}""").SafeParse("1e99999999999999999999").IsSuccess);
        Assert.False(Z.Not(J("""{"const":1}""")).SafeParse("1e99999999999999999999").IsSuccess);
        Assert.False(Z.Not(J("""{"maximum":10}""")).SafeParse("1e99999999999999999999").IsSuccess);
        Assert.True(Z.Any().SafeParse("1e99999999999999999999").IsSuccess);
    }

    private static JsonValueSchema? _tree;

    [Fact]
    public void Failures_kept_by_unions_are_bounded()
    {
        _tree = Z.AnyOf(Z.Boolean(), Z.Null(), Z.Array(Z.Lazy("t", () => _tree!)));
        string json = "0";

        for (int depth = 0; depth < 4; depth++)
        {
            json = "[" + string.Join(',', Enumerable.Repeat(json, 30)) + "]";
        }

        ParseResult<JsonElement> result = _tree.SafeParse(json);

        Assert.False(result.IsSuccess);

        long kept = Count(result.Issues);

        Assert.True(kept < 60_000, $"{kept} issues kept for {json.Length / 1000} KB");
    }

    private static long Count(IEnumerable<ParseIssue> issues)
    {
        long count = 0;

        foreach (ParseIssue issue in issues)
        {
            count++;

            if (issue.Parameters.TryGetValue("errors", out object? errors) && errors is System.Collections.IEnumerable branches)
            {
                foreach (object? branch in branches)
                {
                    count += Count((IEnumerable<ParseIssue>)branch!);
                }
            }
        }

        return count;
    }

    [Fact]
    public async System.Threading.Tasks.Task Exceptions_from_a_callers_refinement_pass_through()
    {
        StringSchema json = Z.String().Refine(_ => throw new JsonException("mine"), "unused");
        ObjectSchema timeout = Z.Object().PatternProperty("^a", Z.String().Refine(_ => throw new System.Text.RegularExpressions.RegexMatchTimeoutException(), "unused"));
        StringSchema later = Z.String().RefineAsync((_, _) => throw new JsonException("mine"), "unused");

        Assert.Equal("mine", Assert.Throws<JsonException>(() => json.SafeParse("\"x\"")).Message);
        Assert.Throws<System.Text.RegularExpressions.RegexMatchTimeoutException>(() => timeout.SafeParse("""{"a":"x"}"""));
        await Assert.ThrowsAsync<JsonException>(async () => await later.SafeParseAsync(new System.IO.MemoryStream("\"x\""u8.ToArray()), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async System.Threading.Tasks.Task A_byte_order_mark_is_read_the_same_from_bytes_and_a_stream()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, 0x31];

        bool fromBytes = Z.Any().SafeParse(bytes).IsSuccess;
        bool fromStream = (await Z.Any().SafeParseAsync(new System.IO.MemoryStream(bytes), TestContext.Current.CancellationToken)).IsSuccess;

        Assert.Equal(fromStream, fromBytes);
        Assert.False(fromBytes);
        Assert.False(Z.Any().SafeParse("Feff1").IsSuccess);
    }

    [Fact]
    public void Strings_that_differ_only_in_how_a_bad_byte_is_read_stay_distinct()
    {
        using JsonDocument document = Bytes("[\"", (byte)0xFF, "\",\"\\udcff\"]");

        Assert.True(Z.Array().UniqueItems().SafeParse(document.RootElement).IsSuccess);
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
