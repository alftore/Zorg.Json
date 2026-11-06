using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;
using Zorg.Json.Emission;
using Zorg.Json.Sampling;
using Zorg.Json.Validation;

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
[Collection(AllocationSensitive.Name)]
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

    [Theory]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("[]")]
    public void Discriminated_union_reports_a_non_object_at_the_root(string json)
    {
        JsonValueSchema union = Z.DiscriminatedUnion("k", Z.Object().Property("k", Z.String().Const("a")));

        ParseIssue issue = Assert.Single(union.SafeParse(json).Issues);

        Assert.Equal(IssueCodes.InvalidType, issue.Code);
        Assert.Equal(string.Empty, issue.InstanceLocation);
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
    public void Union_budget_stops_a_small_hostile_document_quickly()
    {
        string json = string.Concat(Enumerable.Repeat("""{ "n": """, 60)) + "1" + new string('}', 60);
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.False(Ast.Schema.SafeParse(json).IsSuccess);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Took {watch.Elapsed}");
    }

    [Fact]
    public void Union_budget_leaves_room_for_a_large_honest_document()
    {
        string json = $"[{string.Join(',', Enumerable.Range(0, 200_000))}]";

        Assert.True(Z.Array(Z.AnyOf(Z.String(), Z.Boolean(), Z.Integer())).SafeParse(json).IsSuccess);
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

    [Fact]
    public void A_rewrite_keeps_a_lone_surrogate_beside_it()
    {
        Assert.True(Z.Tuple(Z.String().Trim(), Z.Any()).SafeParse("[\" a \", \"\\ud800\"]").IsSuccess);
        Assert.True(Z.Object().Property("x", Z.String().Trim()).Property("y", Z.Any()).SafeParse("{ \"x\": \" a \", \"y\": \"\\ud800\" }").IsSuccess);
    }

    private static string Integers(int count) => $"[{string.Join(',', Enumerable.Repeat("1", count))}]";

    private static string Union(int branches, string failing, string last) =>
        $"{{\"anyOf\":[{string.Concat(Enumerable.Repeat($"{{\"type\":\"{failing}\"}},", branches - 1))}{{\"type\":\"{last}\"}}]}}";

    private static JsonValueSchema Open(string schema) => Z.FromJsonSchema(schema, new() { Formats = Zorg.Json.Import.FormatHandling.AssertKnown });

    [Theory]
    [InlineData(10)]
    [InlineData(12000)]
    public void A_condition_that_runs_out_of_budget_fails_the_parse(int count)
    {
        JsonValueSchema schema = Open($"{{\"if\":{{\"items\":{Union(40, "string", "integer")}}},\"then\":false}}");

        ParseResult<JsonElement> result = schema.SafeParse(Integers(count));

        Assert.False(result.IsSuccess);
        Assert.NotEmpty(result.Issues);
    }

    [Fact]
    public void A_oneOf_that_runs_out_of_budget_fails_the_parse()
    {
        string x = Union(40, "string", "integer");

        Assert.False(Open($"{{\"oneOf\":[{{\"items\":{x}}},{{\"items\":{x}}}]}}").SafeParse(Integers(5000)).IsSuccess);
    }

    [Fact]
    public void Contains_that_runs_out_of_budget_fails_the_parse()
    {
        string schema = $"{{\"contains\":{Union(120, "integer", "boolean")},\"minContains\":0,\"maxContains\":5}}";
        string json = $"[{string.Concat(Enumerable.Repeat("\"\",", 12000))}{string.Join(',', Enumerable.Repeat("true", 10))}]";

        Assert.False(Open(schema).SafeParse(json).IsSuccess);
    }

    private const string SlowPattern = @"^(a+)+$|b\b";

    private static string SlowInput => new string('a', 40) + "b";

    [Fact]
    public void A_regex_timeout_fails_the_parse_wherever_it_happens()
    {
        string pattern = JsonSerializer.Serialize(SlowPattern);
        string input = JsonSerializer.Serialize(SlowInput);

        Assert.False(Open($"{{\"not\":{{\"pattern\":{pattern}}}}}").SafeParse(input).IsSuccess);
        Assert.False(Open($"{{\"if\":{{\"pattern\":{pattern}}},\"then\":false}}").SafeParse(input).IsSuccess);
        Assert.False(Open($"{{\"if\":{{\"pattern\":{pattern}}},\"else\":true}}").SafeParse(input).IsSuccess);
        Assert.False(Open($"{{\"patternProperties\":{{{pattern}:true}}}}").SafeParse($"{{{input}:1}}").IsSuccess);
        Assert.False(Open($"{{\"patternProperties\":{{{pattern}:true}},\"unevaluatedProperties\":false}}").SafeParse($"{{{input}:1}}").IsSuccess);
    }

    [Fact]
    public void One_regex_timeout_ends_the_parse()
    {
        string input = JsonSerializer.Serialize(SlowInput);
        string json = $"[{string.Join(',', Enumerable.Repeat(input, 50))}]";
        JsonValueSchema schema = Open($"{{\"items\":{{\"pattern\":{JsonSerializer.Serialize(SlowPattern)}}}}}");
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.False(schema.SafeParse(json).IsSuccess);

        // One timeout takes 100 ms and 50 would take 5 seconds. The margin is for slow CI runners.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"Took {watch.Elapsed}");
    }

    private static string Nested(int depth) => string.Concat(Enumerable.Repeat("{\"a\":", depth)) + "1" + new string('}', depth);

    [Fact]
    public void Total_budget_stops_a_small_hostile_document()
    {
        JsonValueSchema schema = Open("""{"properties":{"a":{"allOf":[{"$ref":"#"},{"$ref":"#"}]}}}""");
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

        ParseResult<JsonElement> result = schema.SafeParse(Nested(24));

        Assert.False(result.IsSuccess);
        Assert.Equal(IssueCodes.InvalidValue, result.Issues[0].Code);
        // The document is valid, so the failure above proves the limit fired. The time only guards against a hang.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Took {watch.Elapsed}");
    }

    [Fact]
    public void Total_budget_stops_doubling_allOfs_in_an_array()
    {
        string defs = string.Concat(Enumerable.Range(0, 16).Select(n => $"\"d{n}\":{{\"allOf\":[{{\"$ref\":\"#/$defs/d{n + 1}\"}},{{\"$ref\":\"#/$defs/d{n + 1}\"}}]}},"));
        JsonValueSchema schema = Open($"{{\"$defs\":{{{defs}\"d16\":{{\"type\":\"integer\"}}}},\"items\":{{\"$ref\":\"#/$defs/d0\"}}}}");
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.False(schema.SafeParse(Integers(100)).IsSuccess);

        // The document is valid, so the failure above proves the limit fired. The time only guards against a hang.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Took {watch.Elapsed}");
    }

    [Fact]
    public void Total_budget_leaves_room_for_a_large_honest_document()
    {
        JsonValueSchema schema = Open("""
            {"items":{"allOf":[{"type":"object"},{"required":["id"]},{"properties":{"id":{"allOf":[{"type":"integer"},{"minimum":0},{"maximum":999999999}]}}}],
             "if":{"required":["id"]},"then":{"properties":{"id":{"type":"integer"}}}}}
            """);
        string json = $"[{string.Join(',', Enumerable.Range(0, 100_000).Select(n => $"{{\"id\":{n}}}"))}]";

        Assert.True(schema.SafeParse(json).IsSuccess);
    }

    [Fact]
    public void Regex_format_has_a_length_cap()
    {
        JsonValueSchema schema = Open("""{"format":"regex"}""");
        string letters = JsonSerializer.Serialize(string.Concat(Enumerable.Repeat(@"\p{L}", 100_000)));
        string references = JsonSerializer.Serialize("(a)" + string.Concat(Enumerable.Repeat(@"\1", 30_000)));
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.Equal(IssueCodes.InvalidFormat, schema.SafeParse(letters).Issues[0].Code);
        Assert.Equal(IssueCodes.InvalidFormat, schema.SafeParse(references).Issues[0].Code);
        Assert.True(schema.SafeParse(JsonSerializer.Serialize(@"^(a)\1$")).IsSuccess);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"Took {watch.Elapsed}");
    }

    [Fact]
    public void Regex_formats_are_charged_against_the_budget()
    {
        string pattern = JsonSerializer.Serialize(string.Concat(Enumerable.Repeat(@"\p{L}", 150)));
        string json = $"[{string.Join(',', Enumerable.Repeat(pattern, 100))}]";
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

        ParseResult<JsonElement> result = Open("""{"items":{"format":"regex"}}""").SafeParse(json);

        Assert.False(result.IsSuccess);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"Took {watch.Elapsed}");
    }

    [Fact]
    public void Issues_are_capped_with_a_final_issue()
    {
        var result = Z.Array(Z.String()).SafeParse(Integers(1_000_000));

        Assert.False(result.IsSuccess);
        Assert.Equal(ValidationContext.MaxIssues + 1, result.Issues.Length);
        Assert.Equal(IssueCodes.TooManyIssues, result.Issues[^1].Code);
        Assert.All(result.Issues.Take(ValidationContext.MaxIssues), issue => Assert.Equal(IssueCodes.InvalidType, issue.Code));
    }

    [Fact]
    public void Exactly_the_cap_is_not_too_many()
    {
        var result = Z.Array(Z.String()).SafeParse(Integers(ValidationContext.MaxIssues));

        Assert.Equal(ValidationContext.MaxIssues, result.Issues.Length);
        Assert.DoesNotContain(result.Issues, issue => issue.Code == IssueCodes.TooManyIssues);
    }

    [Fact]
    public void Unrecognized_keys_are_capped()
    {
        string json = $"{{{string.Join(',', Enumerable.Range(0, 5000).Select(n => $"\"k{n}\":1"))}}}";

        ParseIssue issue = Assert.Single(Z.Object().Strict().SafeParse(json).Issues);

        Assert.Equal(IssueCodes.UnrecognizedKeys, issue.Code);
        Assert.Equal(ValidationContext.MaxListedKeys, ((string[])issue.Parameters["keys"]!).Length);
        Assert.True(issue.Message.Length < 1000);
    }

    [Fact]
    public void A_given_regex_is_never_stated_as_its_own_text()
    {
        Regex dollar = new("^[a-z]+$");
        Regex digits = new(@"^\d+$");

        Assert.False(Z.String().Pattern(dollar).ToJsonSchema().ContainsKey("pattern"));
        Assert.False(Z.String().Pattern(digits).ToJsonSchema().ContainsKey("pattern"));
        Assert.True(Z.String().Pattern(dollar).SafeParse("\"abc\\n\"").IsSuccess);
        Assert.True(Z.String().Pattern(digits).SafeParse("\"١٢\"").IsSuccess);
        Assert.Equal("^[a-z]+\\n?$", Z.String().Pattern(dollar, "^[a-z]+\\n?$").ToJsonSchema()["pattern"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("uuid", "12345678-1234-1234-1234-123456789abc", true)]
    [InlineData("uuid", "ABCDEF01-1234-1234-1234-123456789ABC", true)]
    [InlineData("uuid", "0x345678-1234-1234-1234-123456789abc", false)]
    [InlineData("uuid", "+2345678-1234-1234-1234-123456789abc", false)]
    [InlineData("uuid", "12345678-0x34-1234-1234-123456789abc", false)]
    [InlineData("uuid", "12345678-1234-1234-1234-12345678-abc", false)]
    [InlineData("iri", "http://example.com/\U0001F600", true)]
    [InlineData("iri", "http://example.com/\U000E1000", true)]
    [InlineData("iri", "http://example.com/\U000E0001", false)]
    [InlineData("iri-reference", "/\U000E0001", false)]
    [InlineData("uri-template", "http://example.com/{var}/{+path,x*}", true)]
    [InlineData("uri-template", "a'b", true)]
    [InlineData("uri-template", "a b", false)]
    [InlineData("idn-hostname", "a。b", true)]
    [InlineData("idn-hostname", "a․b", false)]
    [InlineData("relative-json-pointer", "0+1/a", true)]
    [InlineData("relative-json-pointer", "1-1", true)]
    [InlineData("relative-json-pointer", "2+10#", true)]
    [InlineData("relative-json-pointer", "1/a", true)]
    [InlineData("relative-json-pointer", "+1/a", false)]
    [InlineData("relative-json-pointer", "1+/a", false)]
    [InlineData("relative-json-pointer", "1+01/a", false)]
    [InlineData("relative-json-pointer", "1+1+1/a", false)]
    public void Format_verdicts(string format, string value, bool valid)
    {
        JsonValueSchema schema = Open($"{{\"format\":\"{format}\"}}");

        Assert.Equal(valid, schema.SafeParse(JsonSerializer.Serialize(value)).IsSuccess);
    }

    [Fact]
    public void A_generated_group_name_never_meets_a_real_one()
    {
        StringSchema generatedFirst = Z.String().Pattern(@"^(?<$a>x)(?<group_1>y)\k<$a>$");
        StringSchema realFirst = Z.String().Pattern(@"^(?<group_2>y)(?<$a>x)\k<$a>$");

        Assert.True(generatedFirst.SafeParse("\"xyx\"").IsSuccess);
        Assert.False(generatedFirst.SafeParse("\"xyy\"").IsSuccess);
        Assert.True(realFirst.SafeParse("\"yxx\"").IsSuccess);
        Assert.False(realFirst.SafeParse("\"yxy\"").IsSuccess);
    }

    [Fact]
    public void Many_backreferences_translate_in_linear_time()
    {
        string pattern = "(a)" + string.Concat(Enumerable.Repeat(@"\1", 30_000));
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

        _ = EcmaRegexTranslator.Translate(pattern);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"Took {watch.Elapsed}");
    }

    [Fact]
    public void Astral_property_names_are_encoded_by_code_point()
    {
        ParseIssue issue = Assert.Single(Z.Object().Property("\U0001F600", Z.String()).SafeParse("{\"\U0001F600\":1}").Issues);

        Assert.Equal("#/properties/%F0%9F%98%80/type", issue.SchemaLocation);
    }

    [Fact]
    public void The_sample_generator_judges_with_the_budget_of_a_parse()
    {
        string expensive = $"{{\"allOf\":[{string.Join(',', Enumerable.Repeat("{\"type\":\"integer\"}", 150))}]}}";
        JsonValueSchema schema = Open($"{{\"type\":\"array\",\"minItems\":4000,\"items\":{expensive}}}");

        SampleResult sample = schema.GenerateSample();

        Assert.True(sample.IsValid);
        Assert.True(schema.SafeParse(sample.Value).IsSuccess);
    }

    [Fact]
    public void A_lone_surrogate_property_name_does_not_throw_in_a_lookup()
    {
        JsonElement document = JsonElement.Parse("{\"\\ud800\":1}");
        JsonValueSchema union = Z.DiscriminatedUnion("kind", Z.Object().Property("kind", Z.String().Const("square")));
        ObjectSchema required = Z.Object().Passthrough().Property("a", Z.Integer());
        JsonValueSchema dependent = Open("""{"dependentRequired":{"a":["b"]},"required":["a"]}""");

        Assert.False(union.SafeParse(document).IsSuccess);
        Assert.False(required.SafeParse(document).IsSuccess);
        Assert.False(dependent.SafeParse(document).IsSuccess);
        Assert.False(Open("""{"properties":{"a":true},"required":["a"]}""").SafeParse(document).IsSuccess);
    }

    [Fact]
    public void A_constant_that_is_compared_with_an_undecodable_string_does_not_throw()
    {
        JsonElement document = JsonElement.Parse("{\"\\ud800\":1}");

        Assert.False(Open("""{"const":{"a":1}}""").SafeParse(document).IsSuccess);
        Assert.False(Open("""{"enum":[{"a":1}]}""").SafeParse(document).IsSuccess);
        Assert.True(Open("""{"uniqueItems":true}""").SafeParse(JsonElement.Parse("[{\"\\ud800\":1},{\"a\":1}]")).IsSuccess);
        Assert.False(Open("""{"uniqueItems":true}""").SafeParse(JsonElement.Parse("[\"\\ud800\",\"\\ud800\"]")).IsSuccess);
    }

    [Theory]
    [InlineData("""{"enum":[{"a":1}]}""")]
    [InlineData("""{"const":"a"}""")]
    [InlineData("""{"uniqueItems":true}""")]
    [InlineData("""{"type":"string"}""")]
    [InlineData("""{}""")]
    public void Bytes_that_are_not_utf8_are_invalid_json(string schema)
    {
        foreach (byte[] bytes in new[] { new byte[] { 0x7B, 0x22, 0xFF, 0x22, 0x3A, 0x31, 0x7D }, [0x22, 0xFF, 0x22], [0x5B, 0x22, 0xC3, 0x28, 0x22, 0x5D] })
        {
            ParseResult<JsonElement> result = Open(schema).SafeParse(bytes);

            Assert.Equal(IssueCodes.InvalidJson, Assert.Single(result.Issues).Code);
        }

        Assert.Equal(IssueCodes.InvalidJson, Assert.Single(Z.Any().SafeParse(new byte[] { 0x22, 0xFF, 0x22 }).Issues).Code);
    }

    [Fact]
    public async Task A_stream_that_is_not_utf8_is_invalid_json()
    {
        using System.IO.MemoryStream stream = new([0x22, 0xFF, 0x22]);

        ParseResult<JsonElement> result = await Z.Any().SafeParseAsync(stream, TestContext.Current.CancellationToken);

        Assert.Equal(IssueCodes.InvalidJson, Assert.Single(result.Issues).Code);
    }

    [Fact]
    public void An_escaped_lone_surrogate_is_still_valid_json_text()
    {
        Assert.True(Z.Any().SafeParse("\"\\ud800\"").IsSuccess);
        Assert.True(Z.Any().SafeParse("\"\\ud800\""u8).IsSuccess);
        Assert.True(Z.Any().SafeParse("\"\\ud83d\\ude00\""u8).IsSuccess);
    }

    [Fact]
    public void Builders_reject_text_with_a_lone_surrogate()
    {
        const string Lone = "\ud800";

        Assert.Throws<ArgumentException>(() => Z.Object().Property(Lone, Z.String()));
        Assert.Throws<ArgumentException>(() => Z.String().Const(Lone));
        Assert.Throws<ArgumentException>(() => Z.String().Enum("a", Lone));
        Assert.Throws<ArgumentException>(() => Z.String().Title(Lone));
        Assert.Throws<ArgumentException>(() => Z.String().Description(Lone));
        Assert.Throws<ArgumentException>(() => Z.String().Pattern(Lone));
        Assert.Throws<ArgumentException>(() => Z.Object().DependentRequired("a", Lone));
        Assert.Throws<ArgumentException>(() => Z.DiscriminatedUnion(Lone));
        Assert.Throws<ArgumentException>(() => Z.String().Meta("x-a", JsonElement.Parse("\"\\ud800\"")));
        Assert.Throws<ArgumentException>(() => Z.String().Meta(Lone, JsonElement.Parse("1")));
        Assert.NotNull(Z.String().Const("\U0001F600"));
    }

    [Fact]
    public void A_negative_contains_count_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Z.Array().Contains(Z.Number(), minimum: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Z.Array().Contains(Z.Number(), maximum: -1));
        Assert.NotNull(Z.Array().Contains(Z.Number(), minimum: 0, maximum: 0));
    }

    [Fact]
    public void Deep_items_under_uniqueItems_do_not_throw()
    {
        JsonDocumentOptions deep = new() { MaxDepth = 60_000 };
        string nested = new string('[', 50_000) + new string(']', 50_000);
        JsonElement same = JsonElement.Parse($"[{nested},{nested}]", deep);
        JsonElement different = JsonElement.Parse($"[{nested},[{nested}]]", deep);

        Assert.False(Open("""{"uniqueItems":true}""").SafeParse(same).IsSuccess);
        Assert.True(Open("""{"uniqueItems":true}""").SafeParse(different).IsSuccess);
        Assert.False(Open("""{"const":[[[]]]}""").SafeParse(different.EnumerateArray().Last()).IsSuccess);
        Assert.False(Open("""{"enum":[[[[]]]]}""").SafeParse(same.EnumerateArray().First()).IsSuccess);
        Assert.True(Z.Any().SafeParse(same).IsSuccess);
    }

    [Fact]
    public void Equality_ignores_property_order_and_number_spelling()
    {
        const string Schema = """{"enum":[{"a":1},{"b":[1,2]}]}""";
        JsonValueSchema open = Open("""{"uniqueItems":true}""");

        Assert.False(open.SafeParse("""[{"a":1,"b":[1.0,2]},{"b":[1,2e0],"a":10e-1}]""").IsSuccess);
        Assert.True(open.SafeParse("""[{"a":1,"b":[1,2]},{"a":1,"b":[2,1]}]""").IsSuccess);
        Assert.True(Open(Schema).SafeParse("""{"b":[1.0,2.0]}""").IsSuccess);
    }

    [Fact]
    public void Not_a_string_rejects_a_string_that_can_not_be_read()
    {
        Assert.False(Z.Not(Z.String()).SafeParse("\"\\ud800\"").IsSuccess);
        Assert.False(Open("""{"not":{"type":["null","string"]}}""").SafeParse("\"\\ud800\"").IsSuccess);
        Assert.False(Open("""{"oneOf":[{"type":"string"},{"type":"integer"}]}""").SafeParse("\"\\ud800\"").IsSuccess);
        Assert.True(Z.Not(Z.String()).SafeParse("1").IsSuccess);
        Assert.True(Open("""{"not":{"type":"integer"}}""").SafeParse("\"\\ud800\"").IsSuccess);
    }

    [Fact]
    public void Unicode_normalization_refuses_a_long_run_of_combining_marks()
    {
        string marks = "a" + string.Concat(Enumerable.Repeat("\u0301\u0316", 100_000));
        JsonElement document = JsonSerializer.SerializeToElement(marks);
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

        ParseResult<string> result = Z.String().NormalizeUnicode().SafeParse(document);

        Assert.False(result.IsSuccess);
        Assert.Equal(IssueCodes.InvalidValue, Assert.Single(result.Issues).Code);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), watch.Elapsed.ToString());
        Assert.True(Z.String().NormalizeUnicode().SafeParse("\"e\\u0301\"").IsSuccess);
    }

    [Fact]
    public void Unique_objects_are_checked_in_linear_time()
    {
        string items = string.Join(',', Enumerable.Range(0, 50_000).Select(index => $"{{\"id\":{index},\"tags\":[\"a\",\"b\"]}}"));
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

        ParseResult<JsonElement> result = Open("""{"uniqueItems":true}""").SafeParse($"[{items}]");

        Assert.True(result.IsSuccess);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
        Assert.False(Open("""{"uniqueItems":true}""").SafeParse($"[{items},{{\"tags\":[\"a\",\"b\"],\"id\":7}}]").IsSuccess);
    }

    [Fact]
    public void A_big_enum_is_shared_between_issues()
    {
        string[] values = [.. Enumerable.Range(0, 100_000).Select(index => $"value{index}")];
        StringSchema schema = Z.String().Enum(values);
        ParseResult<IReadOnlyList<string>> result = Z.Array(schema).SafeParse(JsonSerializer.Serialize(Enumerable.Repeat("nope", 50)));

        Assert.False(result.IsSuccess);
        Assert.Equal(50, result.Issues.Length);
        Assert.Same(result.Issues[0].Parameters["values"], result.Issues[49].Parameters["values"]);
        Assert.True(result.Issues[0].Message.Length < 2000);
        Assert.True(schema.SafeParse("\"value99999\"").IsSuccess);
    }

    [Fact]
    public void DependentRequired_on_many_keys_is_not_quadratic()
    {
        const int Count = 50_000;
        string keys = string.Join(',', Enumerable.Range(0, Count).Select(index => $"\"k{index}\":[\"k{(index + 1) % Count}\"]"));
        string properties = string.Join(',', Enumerable.Range(0, Count).Select(index => $"\"k{index}\":1"));
        JsonValueSchema schema = Open($"{{\"dependentRequired\":{{{keys}}},\"required\":[{string.Join(',', Enumerable.Range(0, Count).Select(index => $"\"k{index}\""))}]}}");
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

        ParseResult<JsonElement> result = schema.SafeParse($"{{{properties}}}");

        Assert.True(result.IsSuccess);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
    }

    [Theory]
    [InlineData("1e-999999999")]
    [InlineData("4.9e-325")]
    [InlineData("-1e-400")]
    public void A_number_that_underflows_a_double_is_refused(string json)
    {
        ParseResult<double> result = Z.Number().SafeParse(json);

        Assert.Equal(IssueCodes.InvalidType, Assert.Single(result.Issues).Code);
        Assert.False(Z.Number().Positive().SafeParse(json).IsSuccess);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.000")]
    [InlineData("-0e5")]
    [InlineData("5e-324")]
    public void A_number_that_fits_a_double_is_accepted(string json)
    {
        Assert.True(Z.Number().SafeParse(json).IsSuccess);
        Assert.True(Z.Number().Positive().SafeParse(json).IsSuccess == (json == "5e-324"));
    }

    [Fact]
    public void An_imported_number_stays_exact()
    {
        Assert.True(Open("""{"type":"number","exclusiveMinimum":0}""").SafeParse("1e-999999999").IsSuccess);
    }

    [Fact]
    public void A_negative_node_around_counts_that_strip_changes_is_not_emitted()
    {
        JsonObject emitted = Z.Not(Z.Object().Strip().MinProperties(1)).ToJsonSchema();

        Assert.False(emitted.ContainsKey("not"));
        Assert.True(Z.Not(Z.Object().Strip().MinProperties(1)).SafeParse("{}").IsSuccess);
        Assert.True(Z.Not(Z.Object().Strip().MinProperties(1)).SafeParse("""{"a":1}""").IsSuccess);
    }

    [Fact]
    public void A_negative_node_around_counts_that_default_changes_is_not_emitted()
    {
        ObjectSchema inner = Z.Object().Property("a", Z.Integer().Default(1)).MaxProperties(0);

        Assert.False(Z.Not(inner).ToJsonSchema().ContainsKey("not"));
        Assert.False(Z.If(inner, Z.String()).ToJsonSchema().ContainsKey("if"));
    }

    [Theory]
    [InlineData(JsonSchemaDialect.Draft07)]
    [InlineData(JsonSchemaDialect.OpenApi30)]
    public void A_oneOf_branch_the_dialect_cannot_state_makes_oneOf_an_anyOf(JsonSchemaDialect dialect)
    {
        JsonValueSchema schema = Open("""{"oneOf":[{"type":"array","minItems":1},{"unevaluatedItems":false}]}""");
        JsonSchemaProfile profile = dialect == JsonSchemaDialect.Draft07 ? JsonSchemaProfile.Draft07 : JsonSchemaProfile.OpenApi30;
        JsonObject emitted = schema.ToJsonSchema(new JsonSchemaOptions { Target = profile });

        Assert.False(emitted.ContainsKey("oneOf"));
        Assert.True(emitted.ContainsKey("anyOf"));
        Assert.True(schema.SafeParse("[1]").IsSuccess);
    }

    [Fact]
    public void A_negative_keyword_around_a_dropped_keyword_is_left_out_per_dialect()
    {
        JsonValueSchema schema = Open("""{"not":{"type":"array","unevaluatedItems":false}}""");

        Assert.True(schema.ToJsonSchema().ContainsKey("not"));
        Assert.False(schema.ToJsonSchema(new JsonSchemaOptions { Target = JsonSchemaProfile.Draft07 }).ContainsKey("not"));
        Assert.False(schema.ToJsonSchema(new JsonSchemaOptions { Target = JsonSchemaProfile.OpenApi30 }).ContainsKey("not"));
    }

    [Fact]
    public void An_imported_constant_with_a_lone_surrogate_is_reported_not_written()
    {
        JsonValueSchema schema = Open("""{"const":"\ud800"}""");

        Assert.Throws<InvalidOperationException>(() => schema.ToJsonSchema(new JsonSchemaOptions { Unrepresentable = UnrepresentablePolicy.Throw }));
        Assert.False(schema.ToJsonSchema().ContainsKey("const"));

        EmitResult annotated = schema.Emit(new JsonSchemaOptions { Unrepresentable = UnrepresentablePolicy.Annotate });

        Assert.False(annotated.Schema.ContainsKey("const"));
        Assert.NotEmpty(annotated.Diagnostics);
        Assert.NotNull(annotated.Schema.ToJsonString());
        Assert.False(Open("""{"not":{"enum":["\ud800"]}}""").Emit(new JsonSchemaOptions { Unrepresentable = UnrepresentablePolicy.Annotate }).Schema.ContainsKey("not"));
    }

    [Fact]
    public void A_schema_nested_far_too_deep_is_refused_quickly()
    {
        string schema = string.Concat(Enumerable.Repeat("{\"not\":", 100_000)) + "true" + new string('}', 100_000);
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

        Import.JsonSchemaImportException exception = Assert.Throws<Import.JsonSchemaImportException>(() => Z.FromJsonSchema(schema));

        Assert.Equal(Import.JsonSchemaImportCodes.LimitExceeded, exception.Code);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), watch.Elapsed.ToString());
    }

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    public void A_schema_that_uses_itself_twice_per_level_is_refused_by_emission(int levels)
    {
        ISchema schema = Z.String();

        for (int level = 0; level < levels; level++)
        {
            schema = Z.AllOf(schema, schema);
        }

        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => ((JsonValueSchema)schema).Emit());

        Assert.Contains("Reused = ReusedSchemaHandling.Ref", exception.Message, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
    }

    [Fact]
    public void A_shared_schema_that_is_named_is_emitted_once()
    {
        ISchema schema = Z.String().Named("leaf");

        for (int level = 0; level < 40; level++)
        {
            schema = Z.AllOf(schema, schema).Named($"level{level}");
        }

        JsonObject emitted = ((JsonValueSchema)schema).ToJsonSchema(new JsonSchemaOptions { Reused = ReusedSchemaHandling.Ref });

        Assert.True(emitted.ToJsonString().Length < 20_000);
    }

    [Fact]
    public void Sampling_nested_unsatisfiable_branches_is_cheap()
    {
        ISchema schema = Z.String().MinLength(5).MaxLength(2);

        for (int level = 0; level < 25; level++)
        {
            schema = Z.AnyOf(schema, schema);
        }

        long before = GC.GetTotalAllocatedBytes(precise: true);
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

        SampleResult sample = ((JsonValueSchema)schema).GenerateSample();

        Assert.False(sample.IsValid);
        Assert.True(GC.GetTotalAllocatedBytes(precise: true) - before < 600_000_000, $"{(GC.GetTotalAllocatedBytes(precise: true) - before) / 1_000_000} MB");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
    }

    [Fact]
    public void A_null_string_in_a_list_is_validated_as_null()
    {
        ArraySchema<string> schema = Z.Array(Z.String().Nullable());

        Assert.True(schema.Validate(new List<string> { null!, "a" }).IsSuccess);
        Assert.True(schema.SafeParse("[null,\"a\"]").IsSuccess);
    }

    private static class Ast
    {
        public static readonly ObjectSchema Schema = Z.Object()
            .Property("n", Z.AnyOf(
                Z.Object().Passthrough().Property("n", Z.Lazy("a", () => Schema!)).Property("x", Z.Integer()),
                Z.Object().Passthrough().Property("n", Z.Lazy("a", () => Schema!)).Property("y", Z.Integer())));
    }

    [Theory]
    [InlineData("type")]
    [InlineData("enum")]
    [InlineData("const")]
    [InlineData("properties")]
    [InlineData("$ref")]
    [InlineData("nullable")]
    [InlineData("oneOf")]
    public void Meta_refuses_a_keyword_the_emitter_writes_itself(string keyword)
    {
        Assert.Throws<ArgumentException>(() => Z.String().Meta(keyword, JsonElement.Parse("\"integer\"")));
    }

    [Fact]
    public void Meta_still_takes_a_custom_keyword_and_an_imported_one_is_kept()
    {
        Assert.Contains("\"x-order\":1", Z.String().Meta("x-order", JsonElement.Parse("1")).ToJsonSchema().ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("\"custom\":1", Z.String().Meta("custom", JsonElement.Parse("1")).ToJsonSchema().ToJsonString(), StringComparison.Ordinal);

        JsonValueSchema imported = Z.FromJsonSchema("""{"type":"string","x-order":1,"custom":2}""");

        Assert.Contains("\"x-order\":1", imported.ToJsonSchema().ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("\"custom\":2", imported.ToJsonSchema().ToJsonString(), StringComparison.Ordinal);
    }
}
