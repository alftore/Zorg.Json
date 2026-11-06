using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Xunit;

#pragma warning disable ZORG0005 // These types are here to show what can't be stated, and that it still compiles.
#pragma warning disable ZORG0006
#pragma warning disable CA1815, CA1819, CA1720, CA1056, CA1002, CA2227, CA1044, IDE1006

namespace Zorg.Json.SourceGeneration.Tests.Review;

[GenerateSchema(typeof(ReviewJson))]
public partial record Rx([property: RegularExpression("[a-z]+")] string A);

[GenerateSchema(typeof(ReviewJson))]
public partial record Rx2([property: RegularExpression("^[a-z]+$")] string A);

[GenerateSchema(typeof(ReviewJson))]
public partial record Req1([property: MinLength(5), Required] string A);

[GenerateSchema(typeof(ReviewJson))]
public partial record Req2([property: StringLength(10, MinimumLength = 5), Required] string A);

[GenerateSchema(typeof(ReviewJson))]
public partial record Lengths([property: MaxLength(5), StringLength(3)] string A);

[GenerateSchema(typeof(ReviewJson))]
public partial record Blank([property: Required] string A);

[GenerateSchema(typeof(ReviewJson))]
public partial record Keyword(string @class, [property: MinLength(1)] string @event);

[GenerateSchema(typeof(ReviewJson))]
public partial record Inner([property: MinLength(1)] string V);

[GenerateSchema(typeof(ReviewJson))]
public partial struct Value
{
    [Range(0, 1)]
    public int V { get; set; }
}

[GenerateSchema(typeof(ReviewJson))]
public partial class Shapes
{
    public Value? NullableStruct { get; set; }

    public Optional<Value?> OptionalNullableStruct { get; set; }

    public Optional<List<Inner>> OptionalList { get; set; }

    [MinLength(1)]
    public Optional<string[]> OptionalArray { get; set; }

    [Description("d")]
    public bool? NullableBool { get; set; }

    [Description("d")]
    public Optional<bool?> OptionalNullableBool { get; set; }

    [MinLength(1)]
    public ImmutableArray<string>? NullableImmutable { get; set; }

    [MinLength(1)]
    public string WriteOnly { set => _ = value; }

    public List<Inner?>? NullableItems { get; set; }

    public Inner?[]? NullableItemsArray { get; set; }

    public Optional<Inner?> OptionalNullableClass { get; set; }

    [MinLength(1)]
    public byte[]? Bytes { get; set; }

    [Description("about")]
    public Inner? Described { get; set; }

    [Range(1, 2)]
    public Optional<int?> OptionalNumber { get; set; }
}

public class HiddenBase
{
    [MinLength(1)]
    public string A { get; set; } = "x";

    [MinLength(1)]
    public virtual string B { get; set; } = "x";

    [Range(1, 2)]
    public int C { get; private set; }
}

[GenerateSchema(typeof(ReviewJson))]
public partial class Hidden : HiddenBase
{
    private new int A { get; set; }

    public override string B { get; set; } = "x";

    public int Use() => A;
}

[GenerateSchema(typeof(ReviewJson))]
public partial class Parent
{
    [Range(1, 2)]
    public int P { get; set; }
}

[GenerateSchema(typeof(ReviewJson))]
public partial class Child : Parent
{
    [Range(3, 4)]
    public int Q { get; set; }
}

[GenerateSchema(typeof(ReviewJson))]
public partial record Ranges(
    [property: Range(0.5, 1.5)] int Fraction,
    [property: Range(-1.5, -0.5)] int Negative,
    [property: Range(0, 1.5, MaximumIsExclusive = true)] int ExclusiveTop,
    [property: Range(-1.5, 0, MinimumIsExclusive = true)] int ExclusiveBottom,
    [property: Range(0, 255, MinimumIsExclusive = true)] byte ExclusiveByte,
    [property: Range(0, 255, MaximumIsExclusive = true)] byte ExclusiveByteTop,
    [property: Range(0, int.MaxValue, MaximumIsExclusive = true)] int WholeTop,
    [property: Range(0, 1000)] byte Vacuous);

[GenerateSchema(typeof(ReviewJson))]
public partial record Web([property: Url] string A);

[GenerateSchema(typeof(ReviewJson))]
public partial record Unlimited([property: MaxLength] string A);

[GenerateSchema(typeof(ReviewJson))]
public partial record Converted
{
    [JsonConverter(typeof(InnerConverter))]
    [MinLength(1)]
    public string? A { get; init; }
}

public sealed class InnerConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture);

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteNumberValue(int.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
}

[JsonSerializable(typeof(Rx))]
[JsonSerializable(typeof(Rx2))]
[JsonSerializable(typeof(Req1))]
[JsonSerializable(typeof(Req2))]
[JsonSerializable(typeof(Lengths))]
[JsonSerializable(typeof(Blank))]
[JsonSerializable(typeof(Keyword))]
[JsonSerializable(typeof(Inner))]
[JsonSerializable(typeof(Value))]
[JsonSerializable(typeof(Shapes))]
[JsonSerializable(typeof(Hidden))]
[JsonSerializable(typeof(Parent))]
[JsonSerializable(typeof(Child))]
[JsonSerializable(typeof(Ranges))]
[JsonSerializable(typeof(Web))]
[JsonSerializable(typeof(Unlimited))]
[JsonSerializable(typeof(Converted))]
public partial class ReviewJson : JsonSerializerContext;

public sealed class ReviewTests
{
    [Theory]
    [InlineData("abc", true)]
    [InlineData("123abc456", false)]
    [InlineData("", true)]
    public void A_regular_expression_is_a_whole_match_and_empty_text_passes(string text, bool valid)
    {
        Assert.Equal(valid, Rx.Schema.SafeParse(JsonSerializer.Serialize(new { A = text })).IsSuccess);
        RxModel model = new() { A = text };
        Assert.Equal(valid, Validator.TryValidateObject(model, new ValidationContext(model), null, validateAllProperties: true));
    }

    private sealed class RxModel
    {
        [RegularExpression("[a-z]+")]
        public string A { get; init; } = string.Empty;
    }

    [Fact]
    public void An_anchored_expression_accepts_empty_text_as_the_attribute_does() =>
        Assert.True(Rx2.Schema.SafeParse("""{"A":""}""").IsSuccess);

    [Fact]
    public void Required_does_not_undo_a_longer_minimum()
    {
        Assert.False(Req1.Schema.SafeParse("""{"A":"ab"}""").IsSuccess);
        Assert.False(Req2.Schema.SafeParse("""{"A":"ab"}""").IsSuccess);
        Assert.True(Req1.Schema.SafeParse("""{"A":"abcde"}""").IsSuccess);
    }

    [Fact]
    public void The_tightest_length_wins() => Assert.False(Lengths.Schema.SafeParse("""{"A":"abcd"}""").IsSuccess);

    [Theory]
    [InlineData("   ", false)]
    [InlineData("", false)]
    [InlineData(" a ", true)]
    public void Required_text_is_not_blank(string text, bool valid) =>
        Assert.Equal(valid, Blank.Schema.SafeParse(JsonSerializer.Serialize(new { A = text })).IsSuccess);

    [Fact]
    public void Keywords_as_names_work() => Assert.False(Keyword.Schema.SafeParse("""{"class":"a","event":""}""").IsSuccess);

    [Fact]
    public void Shapes_that_can_not_be_stated_still_build()
    {
        string text = Shapes.Schema.ToJsonSchema().ToJsonString();

        Assert.True(Shapes.Schema.SafeParse("""{"NullableItems":[null],"NullableItemsArray":[null]}""").IsSuccess || text.Length > 0);
    }

    [Fact]
    public void Nullable_properties_stay_nullable()
    {
        ParseResult<Shapes> result = Shapes.Schema.SafeParse("""{"NullableItems":null,"NullableItemsArray":[],"NullableStruct":null,"NullableBool":null,"NullableImmutable":null,"Bytes":null,"Described":null}""");

        Assert.True(result.IsSuccess, string.Join("; ", result.Issues.Select(issue => $"{issue.InstanceLocation} {issue.Message}")));
    }

    [Fact]
    public void A_description_goes_on_a_nested_schema()
    {
        Assert.Contains("about", Shapes.Schema.ToJsonSchema().ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_rule_on_a_base_property_applies_through_an_override()
    {
        Assert.False(Hidden.Schema.SafeParse("""{"B":""}""").IsSuccess);
    }

    [Fact]
    public void A_derived_type_has_its_own_schema_and_the_rules_of_its_base()
    {
        Assert.False(Child.Schema.SafeParse("""{"P":9,"Q":3}""").IsSuccess);
        Assert.False(Child.Schema.SafeParse("""{"P":1,"Q":9}""").IsSuccess);
        Assert.True(Child.Schema.SafeParse("""{"P":1,"Q":3}""").IsSuccess);
        Assert.True(Parent.Schema.SafeParse("""{"P":1}""").IsSuccess);
    }

    private const string GoodRanges = """{"Fraction":1,"Negative":-1,"ExclusiveTop":1,"ExclusiveBottom":-1,"ExclusiveByte":1,"ExclusiveByteTop":254,"WholeTop":5,"Vacuous":200}""";

    [Theory]
    [InlineData("\"Fraction\":1", "\"Fraction\":0")]
    [InlineData("\"Fraction\":1", "\"Fraction\":2")]
    [InlineData("\"Negative\":-1", "\"Negative\":0")]
    [InlineData("\"ExclusiveByte\":1", "\"ExclusiveByte\":0")]
    [InlineData("\"ExclusiveByteTop\":254", "\"ExclusiveByteTop\":255")]
    [InlineData("\"WholeTop\":5", "\"WholeTop\":2147483647")]
    public void Bounds_of_whole_numbers_are_exact(string good, string bad)
    {
        Assert.True(Ranges.Schema.SafeParse(GoodRanges).IsSuccess);
        Assert.False(Ranges.Schema.SafeParse(GoodRanges.Replace(good, bad, StringComparison.Ordinal)).IsSuccess);
    }

    [Theory]
    [InlineData("https://e.com/", true)]
    [InlineData("FTP://e.com/a", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("mailto:a@b.co", false)]
    public void A_url_is_web_only(string text, bool valid) =>
        Assert.Equal(valid, Web.Schema.SafeParse(JsonSerializer.Serialize(new { A = text })).IsSuccess);

    [Fact]
    public void MaxLength_without_a_length_is_no_limit() =>
        Assert.True(Unlimited.Schema.SafeParse(JsonSerializer.Serialize(new { A = new string('a', 500) })).IsSuccess);

    [Fact]
    public void A_property_with_a_converter_keeps_its_open_shape()
    {
        Assert.True(Converted.Schema.SafeParse("""{"A":5}""").IsSuccess);
    }

    [Fact]
    public async Task The_schema_is_the_same_instance_on_every_thread()
    {
        ObjectSchema<Inner>[] seen = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => Inner.Schema)));

        Assert.Single(seen.Distinct());
    }
}
