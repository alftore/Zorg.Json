using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Xunit;
using Zorg.Json.Emission;

namespace Zorg.Json.Tests;

public sealed class Plain
{
    public int Age { get; set; }

    public string? Name { get; set; }

    [JsonIgnore]
    public string? Secret { get; set; }

    public string Upper => (Name ?? string.Empty).ToUpperInvariant();
}

public sealed record Defaulted(string A, string B = "b");

public sealed class Mandatory
{
    public required string Key { get; init; }
}

public sealed class WithConverter
{
    [JsonConverter(typeof(JsonStringEnumConverter<Color>))]
    public Color Hue { get; set; }

    [JsonConverter(typeof(UnixTimeConverter))]
    public DateTime At { get; set; }
}

public sealed class UnixTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTime.UnixEpoch.AddSeconds(reader.GetInt64());

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteNumberValue((long)(value - DateTime.UnixEpoch).TotalSeconds);
}

public sealed class Times
{
    public TimeOnly When { get; set; }

    public DateTime Local { get; set; }
}

public sealed class Heights
{
    public int? Height { get; set; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(NumberedOne), 1)]
public abstract class Numbered;

public sealed class NumberedOne : Numbered
{
    public int A { get; set; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(FirstOnly), "first")]
public abstract class OutOfOrder;

public sealed class FirstOnly : OutOfOrder
{
    public int Radius { get; set; }
}

public sealed class HoldsInterface
{
    public IDisposable? Thing { get; set; }
}

/// <summary>One test per finding of the independent review, so that none comes back.</summary>
public sealed class ReviewRegressionTests
{
    private static string Code<T>(ParseResult<T> result) => result.Issues[0].Code;

    [Theory]
    [InlineData("{\"\\ud800\":1}")]
    [InlineData("[{\"a\":{\"\\udc00\":1}}]")]
    [InlineData("\"\ud800\"")]
    public void Undecodable_input_is_an_issue_for_any_schema(string json) =>
        Assert.False(Z.Object().SafeParse(json).IsSuccess);

    [Fact]
    public void Lone_surrogates_in_every_string_read_are_issues()
    {
        Assert.False(Z.Integer().Coerce().SafeParse("\"\\ud800\"").IsSuccess);
        Assert.False(Z.String().Const("a").SafeParse("\"\\ud800\"").IsSuccess);
        Assert.False(Z.String().Enum("a").SafeParse("\"\\ud800\"").IsSuccess);
        Assert.True(Z.Array().UniqueItems().SafeParse("[\"a\",\"\\ud800\"]").IsSuccess);

        JsonValueSchema union = Z.DiscriminatedUnion("k", Z.Object().Property("k", Z.String().Const("a")));
        Assert.False(union.SafeParse("{\"k\":\"\\ud800\"}").IsSuccess);
    }

    [Fact]
    public void Invalid_utf8_keys_are_issues() =>
        Assert.False(Z.Object().Passthrough().SafeParse([0x7B, 0x22, 0xFF, 0x22, 0x3A, 0x31, 0x7D]).IsSuccess);

    [Fact]
    public void Undecodable_key_of_a_parsed_element_is_an_issue()
    {
        JsonElement element = JsonElement.Parse("{\"\\ud800\":1}");

        Assert.False(Z.Object().Passthrough().SafeParse(element).IsSuccess);
    }

    [Theory]
    [InlineData("1e99999999999")]
    [InlineData("1e2147483648")]
    public void Huge_exponents_are_issues(string json)
    {
        Assert.False(Z.Integer().Const(1).SafeParse(json).IsSuccess);
        Assert.False(Z.Number().Enum(1, 2).SafeParse(json).IsSuccess);
        Assert.True(Z.Array().UniqueItems().SafeParse($"[{json},2]").IsSuccess);
        Assert.False(Z.Integer().Coerce().Const(1).SafeParse($"\"{json}\"").IsSuccess);
    }

    [Fact]
    public void Equal_numbers_are_still_equal() =>
        Assert.False(Z.Array().UniqueItems().SafeParse("[1, 1.0, 1e0]").IsSuccess);

    [Fact]
    public void Deserializer_failures_are_issues()
    {
        Assert.Equal(IssueCodes.DeserializationFailed, Code(Schema.Object<OutOfOrder>().SafeParse("""{ "radius": 1, "$type": "first" }""")));
        Assert.Equal(IssueCodes.DeserializationFailed, Code(Schema.Object<HoldsInterface>().SafeParse("""{ "thing": {} }""")));
    }

    [Fact]
    public void User_exceptions_in_refinements_are_not_swallowed() =>
        Assert.Throws<InvalidOperationException>(() => Z.String().Refine(_ => throw new InvalidOperationException(), "x").SafeParse("\"a\""));

    [Fact]
    public void Odd_discriminators_behave_like_oneOf()
    {
        ObjectSchema nullable = Z.Object().Property("k", Z.String().Enum("a", "b").Nullable());
        Assert.True(Z.DiscriminatedUnion("k", nullable).SafeParse("""{ "k": "a" }""").IsSuccess);
        Assert.True(Z.DiscriminatedUnion("k", Z.Object().Property("k", Z.String().Const("a").Nullable())).SafeParse("""{ "k": "a" }""").IsSuccess);
        Assert.True(Z.DiscriminatedUnion("k", Z.Object().Property("k", Z.Integer().Enum(1, 2))).SafeParse("""{ "k": 1 }""").IsSuccess);

        ObjectSchema target = Z.Object().Property("k", Z.String().Const("a"));
        Assert.True(Z.DiscriminatedUnion("k", Z.Lazy("x", () => target)).SafeParse("""{ "k": "a" }""").IsSuccess);
    }

    [Fact]
    public void Integer_discriminators_select_the_type() =>
        Assert.IsType<NumberedOne>(Schema.Object<Numbered>().Parse("""{ "t": 1, "a": 1 }"""));

    [Fact]
    public void Overlapping_discriminator_values_agree_with_the_emitted_oneOf()
    {
        JsonValueSchema union = Z.DiscriminatedUnion(
            "k",
            Z.Object().Property("k", Z.String().Enum("a", "b")),
            Z.Object().Property("k", Z.String().Enum("a", "c")));

        Assert.Equal(IssueCodes.InvalidUnion, Code(union.SafeParse("""{ "k": "a" }""")));
        Assert.True(union.SafeParse("""{ "k": "b" }""").IsSuccess);
    }

    [Fact]
    public void Union_refinements_see_later_cases()
    {
        ObjectSchema<Circle> circle = Schema.Object(TestJson.Default.Circle);

        Assert.True(Schema.Union<Shape>("kind").Refine(_ => true, "x").Case("circle", circle).SafeParse("""{ "kind": "circle", "radius": 1 }""").IsSuccess);
        Assert.True(Schema.Union<Shape>("kind").Case("a", circle).Refine(_ => true, "x").Case("b", circle).SafeParse("""{ "kind": "b", "radius": 1 }""").IsSuccess);
    }

    [Fact]
    public void Union_without_cases_fails_cleanly()
    {
        Assert.False(Schema.Union<Shape>("kind").SafeParse("1").IsSuccess);
        Assert.False(Schema.Union<Shape>("kind").SafeParse("{}").IsSuccess);
    }

    [Fact]
    public void NormalizeUnicode_keeps_noncharacters() =>
        Assert.True(Z.String().NormalizeUnicode().SafeParse("\"\\ufffe\"").IsSuccess);

    [Fact]
    public void Rewrites_survive_deep_documents()
    {
        JsonDocumentOptions deep = new() { MaxDepth = 200 };
        string nested = new string('[', 100) + new string(']', 100);
        JsonElement element = JsonElement.Parse($$"""{ "deep": {{nested}} }""", deep);

        ParseResult<JsonElement> result = Z.Object().Passthrough().Property("n", Z.Integer().Default(1)).SafeParse(element);

        Assert.Equal(1, result.Value.GetProperty("n").GetInt32());
    }

    [Fact]
    public async Task Async_refinement_that_cannot_deserialize_is_an_issue()
    {
        Schema<OutOfOrder> schema = Schema.Object<OutOfOrder>().RefineAsync((_, _) => ValueTask.FromResult(true), "x");

        ParseResult<OutOfOrder> result = await schema.SafeParseAsync(JsonElement.Parse("""{ "radius": 1, "$type": "first" }"""), TestContext.Current.CancellationToken);

        Assert.Equal(IssueCodes.DeserializationFailed, Code(result));
    }

    [Fact]
    public void Default_element_is_an_issue() =>
        Assert.Equal(IssueCodes.InvalidJson, Code(Z.Any().SafeParse(default(JsonElement))));

    [Fact]
    public void Exponential_union_recursion_stops()
    {
        string json = string.Concat(Enumerable.Repeat("""{ "n": """, 60)) + "1" + new string('}', 60);

        ParseResult<JsonElement> result = Ast.Schema.SafeParse(json);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Case_variants_of_a_property_cannot_bypass_its_rules()
    {
        ObjectSchema<Plain> schema = Schema.Object<Plain>().Passthrough().Property(p => p.Age, s => s.Maximum(5));

        ParseResult<Plain> result = schema.SafeParse("""{ "age": 1, "AGE": 999 }""");

        Assert.Equal(IssueCodes.UnrecognizedKeys, Code(result));
        Assert.Equal(1, schema.Strip().Parse("""{ "age": 1, "AGE": 999 }""").Age);
    }

    [Fact]
    public void Property_only_adds_rules()
    {
        Assert.True(Schema.Object<Plain>().Property(p => p.Age, s => s.Minimum(0)).SafeParse("{}").IsSuccess);
        Assert.True(Schema.Object<Defaulted>().Property(d => d.B, s => s.MaxLength(3)).SafeParse("""{ "a": "x" }""").IsSuccess);
        Assert.True(Schema.Object<Defaulted>().Optional(d => d.A).Property(d => d.A, s => s.MinLength(1)).SafeParse("{}").IsSuccess);
        Assert.True(Schema.Object<Plain>().Property(p => p.Upper, s => s.MinLength(1)).SafeParse("{}").IsSuccess);
    }

    [Fact]
    public void Ignored_properties_are_left_out()
    {
        ObjectSchema<Plain> schema = Schema.Object<Plain>();

        Assert.Throws<ArgumentException>(() => schema.Property(p => p.Secret, s => s));
        Assert.False(schema.ToJsonSchema(new() { Direction = SchemaDirection.Output })["properties"]!.AsObject().ContainsKey("secret"));
    }

    [Fact]
    public void Required_members_cannot_be_made_optional()
    {
        Assert.Throws<ArgumentException>(() => Schema.Object<Mandatory>().Omit(m => m.Key));
        Assert.Throws<ArgumentException>(() => Schema.Object<Mandatory>().Optional(m => m.Key));
    }

    [Fact]
    public void Custom_converters_decide_the_shape()
    {
        ParseResult<WithConverter> result = Schema.Object<WithConverter>().SafeParse("""{ "hue": "DarkBlue", "at": 1000 }""");

        Assert.Equal(Color.DarkBlue, result.Value.Hue);
    }

    [Fact]
    public void Options_level_converters_decide_the_shape()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web) { Converters = { new UnixTimeConverter() } };

        Assert.True(Schema.Object<Times>(options).SafeParse("""{ "local": 1000 }""").IsSuccess);
    }

    [Fact]
    public void Time_types_accept_what_the_serializer_writes()
    {
        ObjectSchema<Times> schema = Schema.Object<Times>();

        Assert.True(schema.SafeParse("""{ "when": "12:00:00", "local": "2020-01-01T00:00:00" }""").IsSuccess);
    }

    [Fact]
    public void Root_collections_are_seeded()
    {
        Assert.True(Schema.Object<Dictionary<string, int>>().SafeParse("""{ "a": 1 }""").IsSuccess);
        Assert.Equal(IssueCodes.InvalidType, Code(Schema.Object<Dictionary<string, int>>().SafeParse("""{ "a": "x" }""")));
        Assert.Equal([1, 2], Schema.Object<List<int>>().Parse("[1, 2]"));
    }

    [Fact]
    public void Nullable_numbers_stay_nullable_with_Const_and_Enum()
    {
        Assert.True(Schema.Object<Heights>().Property(h => h.Height, s => s.Const(5)).SafeParse("""{ "height": null }""").IsSuccess);
        Assert.True(Schema.Object<Heights>().Property(h => h.Height, s => s.Enum(1, 2)).SafeParse("""{ "height": null }""").IsSuccess);
    }

    [Fact]
    public void Property_counts_apply_to_the_result()
    {
        Assert.Equal(IssueCodes.TooSmall, Code(Z.Object().Strip().MinProperties(1).SafeParse("""{ "junk": 1 }""")));
        Assert.True(Z.Object().Strip().Property("a", Z.Integer()).MaxProperties(1).SafeParse("""{ "a": 1, "b": 2 }""").IsSuccess);
        Assert.True(Z.Object().Property("a", Z.Integer().Default(1)).MinProperties(1).SafeParse("{}").IsSuccess);
    }

    [Fact]
    public void UniqueItems_applies_to_the_result() =>
        Assert.Equal("uniqueItems", Z.Array(Z.Integer().Coerce()).UniqueItems().SafeParse("""["1", 1]""").Issues[0].Keyword);

    [Fact]
    public void Applicators_see_the_same_value()
    {
        Assert.False(Z.AllOf(Z.Integer().Coerce(), Z.Integer().Minimum(1)).SafeParse("\"5\"").IsSuccess);
        Assert.False(Z.AllOf(Z.String().Trim(), Z.String().MaxLength(3)).SafeParse("\"  abc  \"").IsSuccess);
        Assert.Equal("a", Z.AllOf(Z.String(), Z.String().Trim()).Parse("\" a \"").GetString());
    }

    [Fact]
    public void Nested_lazy_references_have_their_definitions()
    {
        JsonObject schema = Z.Lazy("a", () => Z.Lazy("b", () => Z.Integer())).ToJsonSchema();

        Assert.Equal("#/$defs/b", schema["$defs"]!["a"]!["$ref"]!.GetValue<string>());
        Assert.True(schema["$defs"]!.AsObject().ContainsKey("b"));
    }

    [Fact]
    public void Inexact_subschemas_loosen_their_keyword()
    {
        Assert.False(Z.Not(Z.String().Trim().MinLength(1)).ToJsonSchema().ContainsKey("not"));
        Assert.False(Z.If(Z.String().Email(), Z.String()).ToJsonSchema().ContainsKey("if"));
        Assert.False(Z.OneOf(Z.String().Email(), Z.String().Uuid()).ToJsonSchema().ContainsKey("oneOf"));
        Assert.True(Z.OneOf(Z.String().Email(), Z.String().Uuid()).ToJsonSchema().ContainsKey("anyOf"));
        Assert.False(Z.Array().Contains(Z.String().Trim(), maximum: 1).ToJsonSchema().ContainsKey("maxContains"));
    }

    [Fact]
    public void Output_schemas_keep_the_keywords() =>
        Assert.True(Z.Not(Z.String().Trim()).ToJsonSchema(new() { Direction = SchemaDirection.Output }).ContainsKey("not"));

    [Fact]
    public void Discriminated_unions_stay_oneOf()
    {
        JsonValueSchema union = Z.DiscriminatedUnion(
            "k",
            Z.Object().Property("k", Z.String().Const("a")).Property("n", Z.Integer()),
            Z.Object().Property("k", Z.String().Const("b")).Property("t", Z.String().Trim()));

        Assert.True(union.ToJsonSchema().ContainsKey("oneOf"));
    }

    [Fact]
    public void Exact_schemas_keep_their_keywords()
    {
        Assert.True(Z.Not(Z.String()).ToJsonSchema().ContainsKey("not"));
        Assert.True(Z.OneOf(Z.String().MinLength(1), Z.Boolean()).ToJsonSchema().ContainsKey("oneOf"));
    }

    [Fact]
    public void Draft07_leaves_out_contains_with_minContains_zero() =>
        Assert.False(Z.Array().Contains(Z.String(), minimum: 0).ToJsonSchema(new() { Target = JsonSchemaProfile.Draft07 }).ContainsKey("contains"));

    [Fact]
    public void OpenApi30_stays_open_without_patternProperties()
    {
        JsonSchemaOptions options = new() { Target = JsonSchemaProfile.OpenApi30 };

        Assert.False(Z.Object().PatternProperty("^x-", Z.String()).ToJsonSchema(options).ContainsKey("additionalProperties"));
    }

    [Fact]
    public void OpenApi30_nullable_goes_beside_each_type()
    {
        JsonObject schema = Z.Number<int>().Coerce().Nullable().ToJsonSchema(new() { Target = JsonSchemaProfile.OpenApi30 });

        Assert.False(schema.ContainsKey("nullable"));
        Assert.All(schema["anyOf"]!.AsArray(), branch => Assert.True(branch!["nullable"]!.GetValue<bool>()));
    }

    [Fact]
    public void Coerced_integers_accept_every_spelling_in_the_input_schema()
    {
        string pattern = Z.Integer().Coerce().ToJsonSchema()["pattern"]!.GetValue<string>();

        Assert.Matches(pattern, "1.0");
        Assert.Matches(pattern, "1e2");
    }

    private static class Ast
    {
        public static readonly ObjectSchema Schema = Z.Object()
            .Property("n", Z.AnyOf(
                Z.Object().Passthrough().Property("n", Z.Lazy("a", () => Schema!)).Property("x", Z.Integer()),
                Z.Object().Passthrough().Property("n", Z.Lazy("a", () => Schema!)).Property("y", Z.Integer())));
    }
}
