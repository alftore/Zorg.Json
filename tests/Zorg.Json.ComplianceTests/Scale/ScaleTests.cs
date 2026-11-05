using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;
using Zorg.Json.Import;

namespace Zorg.Json.ComplianceTests.Scale;

/// <summary>Limits and deep input: an error is fine, a crash or a stray exception is not.</summary>
public sealed class ScaleTests
{
    private static readonly JsonDocumentOptions Unlimited = new() { MaxDepth = int.MaxValue };

    public static TheoryData<string, string, string> Nestings { get; } = new()
    {
        { "not", """{"not":""", "}" },
        { "properties", """{"properties":{"a":""", "}}" },
        { "items", """{"items":""", "}" },
        { "allOf", """{"allOf":[""", "]}" },
        { "anyOf", """{"anyOf":[""", "]}" },
        { "additionalProperties", """{"additionalProperties":""", "}" },
    };

    private static string Nest(string open, string close, string inner, int depth) =>
        string.Concat(Enumerable.Repeat(open, depth)) + inner + string.Concat(Enumerable.Repeat(close, depth));

    private static JsonElement Deep(string text) => JsonDocument.Parse(text, Unlimited).RootElement;

    private static JsonSchemaImportException Refused(string document, JsonSchemaImportOptions? options = null) =>
        Assert.Throws<JsonSchemaImportException>(() => Z.FromJsonSchema(document, options));

    [Theory]
    [MemberData(nameof(Nestings))]
    public void Import_accepts_MaxDepth_and_refuses_one_more(string name, string open, string close)
    {
        JsonValueSchema accepted = Z.FromJsonSchema(Nest(open, close, "{}", 63));

        Assert.False(string.IsNullOrEmpty(name));
        Assert.NotNull(accepted);

        foreach (int depth in new[] { 65, 66, 200, 5_000 })
        {
            Assert.Equal(JsonSchemaImportCodes.LimitExceeded, Refused(Nest(open, close, "{}", depth)).Code);
        }
    }

    [Fact]
    public void Import_depth_boundary_is_exactly_MaxDepth()
    {
        Assert.NotNull(Z.FromJsonSchema(Nest("""{"not":""", "}", "{}", 64)));
        Assert.Equal(JsonSchemaImportCodes.LimitExceeded, Refused(Nest("""{"not":""", "}", "{}", 65)).Code);
    }

    [Fact]
    public void Import_depth_can_be_raised()
    {
        JsonSchemaImportOptions options = new() { MaxDepth = 300 };
        JsonValueSchema schema = Z.FromJsonSchema(Nest("""{"properties":{"a":""", "}}", """{"type":"string"}""", 250), options);

        Assert.True(schema.SafeParse(Deep(Nest("""{"a":""", "}", "\"x\"", 250))).IsSuccess);
        Assert.False(schema.SafeParse(Deep(Nest("""{"a":""", "}", "1", 250))).IsSuccess);
        Assert.NotNull(Z.FromJsonSchema(Nest("""{"not":""", "}", "{}", 300), options));
    }

    [Fact]
    public void Import_node_limit_has_one_boundary_at_MaxNodes()
    {
        static string Wide(int count) => """{"allOf":[""" + string.Join(',', Enumerable.Repeat("{}", count)) + "]}";

        int boundary = Enumerable.Range(9_990, 20).First(count => !Imports(Wide(count)));

        Assert.Equal(10_000, boundary);
        Assert.Equal(JsonSchemaImportCodes.LimitExceeded, Refused(Wide(boundary)).Code);
        Assert.True(Imports(Wide(boundary), new() { MaxNodes = 20_000 }));
        Assert.Equal(JsonSchemaImportCodes.LimitExceeded, Refused(Wide(100), new() { MaxNodes = 50 }).Code);
    }

    [Fact]
    public void Import_pattern_length_has_one_boundary_at_MaxPatternLength()
    {
        static string Pattern(int length) => "{\"pattern\":\"" + new string('a', length) + "\"}";

        Assert.True(Imports(Pattern(1_024)));
        Assert.Equal(JsonSchemaImportCodes.LimitExceeded, Refused(Pattern(1_025)).Code);
        Assert.Equal(JsonSchemaImportCodes.LimitExceeded, Refused(Pattern(2_000_000)).Code);
        Assert.True(Imports(Pattern(2_000), new() { MaxPatternLength = 4_096 }));
        Assert.Equal(JsonSchemaImportCodes.LimitExceeded, Refused(Pattern(11), new() { MaxPatternLength = 10 }).Code);
    }

    [Fact]
    public void Import_counts_patterns_under_patternProperties_and_in_every_dialect()
    {
        string patternProperties = "{\"patternProperties\":{\"" + new string('a', 1_100) + "\":{}}}";

        // OpenAPI 3.0 has no patternProperties, so there it's an inert keyword and holds no pattern.
        foreach (JsonSchemaImportDialect dialect in System.Enum.GetValues<JsonSchemaImportDialect>().Where(item => item != JsonSchemaImportDialect.OpenApi30))
        {
            Assert.Equal(JsonSchemaImportCodes.LimitExceeded, Refused(patternProperties, new() { DefaultDialect = dialect }).Code);
        }
    }

    [Fact]
    public void Reference_chains_longer_than_MaxDepth_are_refused()
    {
        static string Chain(int length) =>
            """{"$ref":"#/$defs/d0","$defs":{""" + string.Join(',', Enumerable.Range(0, length).Select(index => $"\"d{index}\":{{\"$ref\":\"#/$defs/d{index + 1}\"}}"))
            + $",\"d{length}\":{{\"type\":\"string\"}}}}}}";

        Assert.True(Imports(Chain(30)));
        Assert.Equal(JsonSchemaImportCodes.LimitExceeded, Refused(Chain(500)).Code);
    }

    [Fact]
    public void Reference_cycles_are_refused_or_terminate()
    {
        string[] documents =
        [
            """{"$defs":{"a":{"$ref":"#/$defs/b"},"b":{"$ref":"#/$defs/a"}},"$ref":"#/$defs/a"}""",
            """{"$ref":"#"}""",
            """{"allOf":[{"$ref":"#"}]}""",
            """{"not":{"$ref":"#"}}""",
            """{"$defs":{"a":{"allOf":[{"$ref":"#/$defs/a"}]}},"$ref":"#/$defs/a"}""",
        ];

        foreach (string document in documents)
        {
            try
            {
                JsonValueSchema schema = Z.FromJsonSchema(document);
                _ = schema.SafeParse("1");
                _ = schema.SafeParse("{}");
                _ = schema.Emit();
            }
            catch (JsonSchemaImportException)
            {
            }
        }
    }

    [Fact]
    public void Text_nested_beyond_the_reader_limit_is_invalid_json_not_an_exception()
    {
        string deep = new string('[', 100_000) + new string(']', 100_000);
        string deepObjects = Nest("""{"a":""", "}", "1", 50_000);

        foreach (string text in new[] { deep, deepObjects })
        {
            Assert.Equal("invalid_json", Assert.Single(Z.Any().SafeParse(text).Issues).Code);
            Assert.Equal("invalid_json", Assert.Single(Z.Any().SafeParse(Encoding.UTF8.GetBytes(text)).Issues).Code);
            Assert.Equal("invalid_json", Assert.Single(Z.Object().Passthrough().SafeParse(text).Issues).Code);
        }
    }

    [Fact]
    public void Elements_deeper_than_the_reader_limit_are_walked_without_a_crash()
    {
        JsonElement arrays = Deep(new string('[', 1_000) + new string(']', 1_000));
        JsonElement objects = Deep(Nest("""{"a":""", "}", "1", 1_000));

        Assert.True(Z.Any().SafeParse(arrays).IsSuccess);
        Assert.True(Z.Array().SafeParse(arrays).IsSuccess);
        Assert.True(Z.Object().Passthrough().SafeParse(objects).IsSuccess);
        Assert.False(Z.Object().SafeParse(objects).IsSuccess);
        Assert.False(Z.Object().Property("a", Z.Number()).SafeParse(objects).IsSuccess);
        Assert.True(Z.Array().UniqueItems().SafeParse(arrays).IsSuccess);
        Assert.False(Z.String().SafeParse(arrays).IsSuccess);
    }

    [Fact]
    public void A_recursive_schema_stops_at_its_depth_limit_with_an_issue()
    {
        ISchema? tree = null;
        JsonValueSchema lazy = Z.Lazy("tree", () => tree!);
        tree = Z.Object().Property("c", Z.Array(lazy).Optional()).Optional();

        JsonElement shallow = Deep(Nest("""{"c":[""", "]}", "{}", 100));
        JsonElement deep = Deep(Nest("""{"c":[""", "]}", "{}", 1_000));

        Assert.True(lazy.SafeParse(shallow).IsSuccess);

        ParseResult<JsonElement> result = lazy.SafeParse(deep);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Issues, issue => issue.Code == "invalid_value");
    }

    [Fact]
    public void A_typed_recursive_schema_reports_the_serializer_depth_limit_as_an_issue()
    {
        ParseResult<Category> shallow = Categories.Schema.SafeParse(Deep(Nest("""{"name":"a","children":[""", "]}", """{"name":"b","children":[]}""", 10)));
        ParseResult<Category> deep = Categories.Schema.SafeParse(Deep(Nest("""{"name":"a","children":[""", "]}", """{"name":"b","children":[]}""", 100)));

        Assert.True(shallow.IsSuccess);
        Assert.False(deep.IsSuccess);
        Assert.Equal("deserialization_failed", Assert.Single(deep.Issues).Code);
    }

    [Fact]
    public void A_typed_chain_checks_every_level()
    {
        string valid = Nest("""{"id":"a","next":""", "}", "null", 20);
        string invalid = Nest("""{"id":"a","next":""", "}", """{"id":1}""", 20);

        Assert.True(Chains.Schema.SafeParse(valid).IsSuccess);
        ParseResult<Chain> result = Chains.Schema.SafeParse(invalid);

        Assert.False(result.IsSuccess);
        string location = result.Issues.Single().InstanceLocation;

        Assert.Equal("/next", location);
        Assert.Equal("invalid_union", result.Issues.Single().Code);
    }

    [Fact]
    public void Long_strings_are_measured_and_checked()
    {
        string text = new('a', 5_000_000);
        string json = "\"" + text + "\"";

        Assert.True(Z.String().MinLength(5_000_000).MaxLength(5_000_000).SafeParse(json).IsSuccess);
        Assert.False(Z.String().MaxLength(4_999_999).SafeParse(json).IsSuccess);
        Assert.True(Z.String().Pattern("^a+$").SafeParse(json).IsSuccess);
        Assert.False(Z.String().Uuid().SafeParse(json).IsSuccess);
        Assert.False(Z.String().Email().SafeParse(json).IsSuccess);
        Assert.False(Z.String().Date().SafeParse(json).IsSuccess);
        Assert.True(Z.String().Trim().MinLength(1).SafeParse(json).IsSuccess);
        Assert.True(Z.String().Const(text).SafeParse(json).IsSuccess);
    }

    [Fact]
    public void Emoji_strings_count_code_points_at_scale()
    {
        string json = "\"" + string.Concat(Enumerable.Repeat("😀", 1_000_000)) + "\"";

        Assert.True(Z.String().MinLength(1_000_000).MaxLength(1_000_000).SafeParse(json).IsSuccess);
        Assert.False(Z.String().MaxLength(999_999).SafeParse(json).IsSuccess);
    }

    [Fact]
    public void Large_arrays_and_objects_parse()
    {
        string array = "[" + string.Join(',', Enumerable.Range(0, 200_000)) + "]";
        string wide = "{" + string.Join(',', Enumerable.Range(0, 100_000).Select(index => $"\"k{index}\":{index}")) + "}";

        Assert.True(Z.Array(Z.Integer()).SafeParse(array).IsSuccess);
        Assert.True(Z.Array(Z.Integer()).UniqueItems().SafeParse(array).IsSuccess);
        Assert.False(Z.Array(Z.Integer()).MaxItems(199_999).SafeParse(array).IsSuccess);
        Assert.True(Z.Record(Z.Integer()).SafeParse(wide).IsSuccess);
        Assert.True(Z.Object().Passthrough().MinProperties(100_000).SafeParse(wide).IsSuccess);
    }

    private static bool Imports(string document, JsonSchemaImportOptions? options = null)
    {
        try
        {
            _ = Z.FromJsonSchema(document, options);
            return true;
        }
        catch (JsonSchemaImportException)
        {
            return false;
        }
    }

    private static class Categories
    {
        public static readonly ObjectSchema<Category> Schema = Zorg.Json.Schema.Object(ScaleJson.Default.Category)
            .Property(category => category.Name, name => name.MinLength(1))
            .Property(category => category.Children, children => children.Items(Zorg.Json.Schema.Lazy<Category>("category", () => Schema!)));
    }

    private static class Chains
    {
        public static readonly ObjectSchema<Chain> Schema = Zorg.Json.Schema.Object(ScaleJson.Default.Chain)
            .Property(chain => chain.Next, Zorg.Json.Schema.Lazy<Chain>("chain", () => Schema!));
    }
}
