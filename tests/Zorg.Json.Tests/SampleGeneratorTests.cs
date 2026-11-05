using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Xunit;
using Zorg.Json.Sampling;

namespace Zorg.Json.Tests;

#pragma warning disable CA1819 // The contract needs a byte array.
public sealed record Event(string Title, DateTime At, TimeOnly Start, TimeSpan Length, DateOnly Day, DateTimeOffset Stamp, byte[] Data, Uri Link, Guid Id, char Mark);
#pragma warning restore CA1819

[Flags]
public enum Access
{
    None = 0,
    Read = 1,
}

public sealed record Flagged(Access Access, int? Count, long Big, Color Color);

public sealed record WithComputed(string Name)
{
    public string Upper => Name.ToUpperInvariant();
}

[JsonSerializable(typeof(Event))]
[JsonSerializable(typeof(Flagged))]
[JsonSerializable(typeof(WithComputed))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
public sealed partial class SampleJson : JsonSerializerContext;

public sealed class SampleGeneratorTests
{
    private static readonly ObjectSchema Tree = Z.Object()
        .Property("value", Z.Integer())
        .Property("children", Z.Array(Z.Lazy("tree", () => Tree!)).Optional());

    private static readonly JsonValueSchema InPlaceCycle = Z.AnyOf(Z.Lazy("cycle", () => InPlaceCycle!), Z.String());

    private static readonly ObjectSchema Nullable = Z.Object().Property("self", Z.AnyOf(Z.Lazy("nullable", () => Nullable!), Z.Null()));

    private static string Valid(ISchema schema, SampleOptions? options = null)
    {
        SampleResult sample = schema.GenerateSample(options);
        Assert.True(sample.IsValid, $"{sample.Value.GetRawText()}: {sample.Issues.ToPrettyString()}");
        Assert.Empty(sample.Issues);
        return sample.Value.GetRawText();
    }

    private static string Valid(string schema, SampleOptions? options = null) => Valid(Z.FromJsonSchema(schema), options);

    private static void Unsatisfiable(string schema)
    {
        SampleResult sample = Z.FromJsonSchema(schema).GenerateSample();
        Assert.False(sample.IsValid, sample.Value.GetRawText());
        Assert.NotEmpty(sample.Issues);
    }

    [Theory]
    [InlineData("""{ "const": 5, "default": 6 }""", "5")]
    [InlineData("""{ "default": 6, "examples": [7], "enum": [6, 7, 8] }""", "6")]
    [InlineData("""{ "examples": [7, 8], "enum": [7, 8] }""", "7")]
    [InlineData("""{ "enum": ["a", "b"] }""", "\"a\"")]
    [InlineData("""{ "type": "integer", "default": "no", "examples": [1.5, 3] }""", "3")]
    [InlineData("""{ "enum": [1, 2], "default": 3, "not": { "const": 1 } }""", "2")]
    [InlineData("""{ "type": "string", "default": 1 }""", "\"string\"")]
    public void Literals_come_first_and_must_validate(string schema, string expected) => Assert.Equal(expected, Valid(schema));

    [Theory]
    [InlineData("date")]
    [InlineData("date-time")]
    [InlineData("duration")]
    [InlineData("email")]
    [InlineData("hostname")]
    [InlineData("idn-email")]
    [InlineData("idn-hostname")]
    [InlineData("ipv4")]
    [InlineData("ipv6")]
    [InlineData("iri")]
    [InlineData("iri-reference")]
    [InlineData("json-pointer")]
    [InlineData("regex")]
    [InlineData("relative-json-pointer")]
    [InlineData("time")]
    [InlineData("uri")]
    [InlineData("uri-reference")]
    [InlineData("uri-template")]
    [InlineData("uuid")]
    public void Every_format_has_samples(string format)
    {
        ISchema schema = Z.String().Format(format);
        Assert.Equal(SampleStrings.Format(format, 0), JsonDocument.Parse(Valid(schema)).RootElement.GetString());

        string[] several = [.. JsonDocument.Parse(Valid(Z.Array(Z.String().Format(format)).MinItems(20).UniqueItems())).RootElement.EnumerateArray().Select(item => item.GetString()!)];
        Assert.Equal(20, several.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("email", 40, null)]
    [InlineData("hostname", 30, null)]
    [InlineData("uri", 60, null)]
    [InlineData("uri-template", 60, null)]
    [InlineData("json-pointer", 30, null)]
    [InlineData("regex", 30, null)]
    [InlineData("email", null, 5)]
    [InlineData("ipv6", null, 3)]
    [InlineData("uri", null, 4)]
    public void Formats_meet_lengths(string format, int? minimum, int? maximum)
    {
        StringSchema schema = Z.String().Format(format);
        schema = minimum is { } least ? schema.MinLength(least) : schema;
        schema = maximum is { } most ? schema.MaxLength(most) : schema;
        Valid(schema);
    }

    [Theory]
    [InlineData("""{ "type": "string" }""", "\"string\"")]
    [InlineData("""{ "type": "string", "maxLength": 3 }""", "\"str\"")]
    [InlineData("""{ "type": "string", "minLength": 8 }""", "\"stringst\"")]
    [InlineData("""{ "type": "string", "maxLength": 0 }""", "\"\"")]
    [InlineData("""{ "minLength": 2, "maxLength": 2 }""", "\"st\"")]
    public void Plain_strings_fit_their_length(string schema, string expected) => Assert.Equal(expected, Valid(schema));

    [Theory]
    [InlineData("^abc$", "abc")]
    [InlineData("^[a-z]+$", "a")]
    [InlineData("^[A-Z]{3}$", "AAA")]
    [InlineData("^\\d{4}-\\d{2}$", "0000-00")]
    [InlineData("^\\w\\s\\w$", "a a")]
    [InlineData("^a.c$", "aac")]
    [InlineData("^(foo|bar)baz$", "foobaz")]
    [InlineData("^(?:ab)+c?$", "ab")]
    [InlineData("^(?<year>\\d{2})$", "00")]
    [InlineData("^[^a-z]$", "A")]
    [InlineData("^[\\d_]+$", "_")]
    [InlineData("^\\p{Lu}$", "A")]
    [InlineData("^\\u0041\\x42\\u{43}$", "ABC")]
    [InlineData("^\\.\\*\\/$", ".*/")]
    [InlineData("^a{2,}b{1,3}$", "aab")]
    [InlineData("^😀+$", "😀")]
    [InlineData("^[\\u4e00-\\u9fff]$", "一")]
    [InlineData("abc", "abc")]
    public void Patterns_are_generated(string pattern, string expected)
    {
        string sample = Valid(Z.String().Pattern(pattern));
        Assert.Equal(expected, JsonDocument.Parse(sample).RootElement.GetString());
    }

    [Theory]
    [InlineData("^[a-z]+$", 5, 5)]
    [InlineData("^(ab)+$", 6, 8)]
    [InlineData("^x*$", 3, null)]
    [InlineData("abc", 10, null)]
    [InlineData("abc$", 10, null)]
    [InlineData("^abc", 10, null)]
    [InlineData("^(a|bbbb)$", null, 1)]
    [InlineData("^(bbbb|a)$", null, 1)]
    [InlineData("^😀+$", 3, 3)]
    public void Patterns_meet_lengths(string pattern, int? minimum, int? maximum)
    {
        StringSchema schema = Z.String().Pattern(pattern);
        schema = minimum is { } least ? schema.MinLength(least) : schema;
        schema = maximum is { } most ? schema.MaxLength(most) : schema;
        Valid(schema);
    }

    [Fact]
    public void Two_patterns_can_share_a_string() =>
        Valid("""{ "type": "string", "allOf": [{ "pattern": "^ab" }, { "pattern": "cd$" }] }""");

    [Theory]
    [InlineData("^(?=xy)xyz$")]
    [InlineData("^(?!b)xyz$")]
    [InlineData("^(?<=a)b")]
    [InlineData("^(a)\\1$")]
    [InlineData("^(?<x>a)\\k<x>$")]
    public void Lookarounds_and_backreferences_are_not_generated(string pattern) =>
        Assert.False(Z.String().Pattern(pattern).GenerateSample().IsValid);

    [Theory]
    [InlineData("""{ "type": "number" }""", "0")]
    [InlineData("""{ "type": "integer", "minimum": -5, "maximum": 5 }""", "0")]
    [InlineData("""{ "type": "number", "minimum": 5 }""", "5")]
    [InlineData("""{ "type": "number", "maximum": -5 }""", "-5")]
    [InlineData("""{ "type": "integer", "exclusiveMinimum": 5 }""", "6")]
    [InlineData("""{ "type": "integer", "exclusiveMaximum": -5 }""", "-6")]
    [InlineData("""{ "type": "number", "exclusiveMinimum": 0 }""", "1")]
    [InlineData("""{ "type": "number", "exclusiveMinimum": 0, "exclusiveMaximum": 1 }""", "0.5")]
    [InlineData("""{ "type": "number", "exclusiveMinimum": 1.1, "exclusiveMaximum": 1.2 }""", "1.15")]
    [InlineData("""{ "type": "integer", "minimum": 0.5 }""", "1")]
    [InlineData("""{ "type": "integer", "maximum": -0.5 }""", "-1")]
    [InlineData("""{ "type": "number", "minimum": 7, "multipleOf": 5 }""", "10")]
    [InlineData("""{ "type": "number", "maximum": -7, "multipleOf": 5 }""", "-10")]
    [InlineData("""{ "type": "number", "minimum": 0.15, "multipleOf": 0.1 }""", "0.2")]
    [InlineData("""{ "type": "number", "minimum": 0.001, "multipleOf": 0.0001 }""", "0.001")]
    [InlineData("""{ "type": "integer", "minimum": 1, "multipleOf": 1.5 }""", "3")]
    [InlineData("""{ "type": "number", "exclusiveMinimum": 10, "multipleOf": 10 }""", "20")]
    [InlineData("""{ "type": "integer", "minimum": 100000000000000000000000000000 }""", "100000000000000000000000000000")]
    [InlineData("""{ "type": "integer", "exclusiveMinimum": 100000000000000000000000000000 }""", "100000000000000000000000000001")]
    [InlineData("""{ "type": "number", "minimum": 1e400 }""", "1e400")]
    [InlineData("""{ "type": "integer", "minimum": 1e1000000000 }""", "1e1000000000")]
    [InlineData("""{ "type": "number", "exclusiveMinimum": 1e1000000000 }""", "2e1000000000")]
    [InlineData("""{ "type": "number", "exclusiveMaximum": -1e-1000000000 }""", "-2e-1000000000")]
    [InlineData("""{ "type": "number", "exclusiveMinimum": 0, "exclusiveMaximum": 1e-500 }""", "5e-501")]
    [InlineData("""{ "type": "number", "minimum": 12345678901234567890.5, "multipleOf": 0.25 }""", "12345678901234567890.5")]
    [InlineData("""{ "minimum": 3 }""", "3")]
    public void Numbers_are_exact(string schema, string expected) => Assert.Equal(expected, Valid(schema));

    [Theory]
    [InlineData("""{ "type": "integer", "exclusiveMinimum": 1, "exclusiveMaximum": 2 }""")]
    [InlineData("""{ "type": "number", "minimum": 2, "maximum": 1 }""")]
    [InlineData("""{ "type": "number", "minimum": 1, "maximum": 2, "multipleOf": 5 }""")]
    [InlineData("""{ "type": "number", "exclusiveMinimum": 1, "exclusiveMaximum": 1 }""")]
    [InlineData("""{ "type": "string", "minLength": 2, "maxLength": 1 }""")]
    [InlineData("""{ "type": "array", "minItems": 2, "maxItems": 1 }""")]
    [InlineData("""{ "type": "array", "items": false, "minItems": 1 }""")]
    [InlineData("""{ "type": "array", "items": { "enum": [1, 2] }, "uniqueItems": true, "minItems": 3 }""")]
    [InlineData("""{ "type": "array", "contains": { "const": 1 }, "maxContains": 0 }""")]
    [InlineData("""{ "type": "object", "required": ["a"], "properties": { "a": false } }""")]
    [InlineData("""{ "type": "object", "minProperties": 1, "additionalProperties": false }""")]
    [InlineData("""{ "type": "object", "required": ["a"], "additionalProperties": false }""")]
    [InlineData("""{ "type": "object", "required": ["a", "b"], "maxProperties": 1 }""")]
    [InlineData("""{ "allOf": [{ "type": "string" }, { "type": "integer" }] }""")]
    [InlineData("""{ "allOf": [{ "const": 1 }, { "const": 2 }] }""")]
    [InlineData("""{ "enum": [1], "not": { "const": 1 } }""")]
    [InlineData("""{ "oneOf": [{ "type": "integer" }, { "type": "integer" }] }""")]
    [InlineData("""{ "not": {} }""")]
    [InlineData("""{ "$defs": { "a": { "type": "object", "required": ["next"], "properties": { "next": { "$ref": "#/$defs/a" } } } }, "$ref": "#/$defs/a" }""")]
    [InlineData("false")]
    public void Unsatisfiable_schemas_are_reported(string schema) => Unsatisfiable(schema);

    [Fact]
    public void The_closest_attempt_is_returned()
    {
        SampleResult sample = Z.FromJsonSchema("""{ "type": "object", "required": ["a", "b"], "properties": { "a": { "type": "integer" }, "b": false } }""").GenerateSample();
        Assert.False(sample.IsValid);
        Assert.Equal(JsonValueKind.Object, sample.Value.ValueKind);
        Assert.Equal(0, sample.Value.GetProperty("a").GetInt32());
        Assert.Equal(JsonValueKind.Null, Z.Never().GenerateSample().Value.ValueKind);
    }

    [Theory]
    [InlineData("""{ "type": "array" }""", "[]")]
    [InlineData("""{ "type": "array", "items": { "type": "integer" } }""", "[0]")]
    [InlineData("""{ "type": "array", "items": { "type": "integer" }, "minItems": 3 }""", "[0,0,0]")]
    [InlineData("""{ "type": "array", "items": { "type": "integer" }, "maxItems": 0 }""", "[]")]
    [InlineData("""{ "type": "array", "items": { "type": "integer" }, "minItems": 3, "uniqueItems": true }""", "[0,1,2]")]
    [InlineData("""{ "type": "array", "items": { "type": "boolean" }, "minItems": 2, "uniqueItems": true }""", "[false,true]")]
    [InlineData("""{ "type": "array", "items": { "enum": ["a", "b"] }, "minItems": 2, "uniqueItems": true }""", "[\"a\",\"b\"]")]
    [InlineData("""{ "type": "array", "prefixItems": [{ "type": "integer" }, { "type": "string" }] }""", "[0,\"string\"]")]
    [InlineData("""{ "type": "array", "prefixItems": [{ "type": "integer" }], "items": false, "minItems": 1 }""", "[0]")]
    [InlineData("""{ "type": "array", "prefixItems": [{ "const": 1 }], "items": { "const": 2 }, "minItems": 3 }""", "[1,2,2]")]
    [InlineData("""{ "type": "array", "contains": { "const": 5 } }""", "[5]")]
    [InlineData("""{ "type": "array", "items": { "type": "integer" }, "contains": { "minimum": 5 } }""", "[5]")]
    [InlineData("""{ "type": "array", "contains": { "const": 5 }, "minContains": 2 }""", "[5,5]")]
    [InlineData("""{ "type": "array", "contains": { "const": 5 }, "minContains": 0 }""", "[]")]
    [InlineData("""{ "type": "array", "items": { "type": "integer" }, "contains": { "const": 5 }, "maxContains": 1, "minItems": 3 }""", "[5,0,0]")]
    [InlineData("""{ "type": "array", "items": { "type": "integer" }, "contains": { "const": 0 }, "minContains": 0, "maxContains": 0, "minItems": 1 }""", "[1]")]
    public void Arrays(string schema, string expected) => Assert.Equal(expected, Valid(schema));

    [Theory]
    [InlineData("""{ "type": "array", "items": { "type": "object", "properties": { "a": { "type": "integer" } }, "required": ["a"] }, "minItems": 3, "uniqueItems": true }""")]
    [InlineData("""{ "type": "array", "items": { "type": "array", "items": { "type": "integer" } }, "minItems": 3, "uniqueItems": true }""")]
    [InlineData("""{ "type": "array", "items": { "type": "string", "pattern": "^[a-c]$" }, "minItems": 3, "uniqueItems": true }""")]
    [InlineData("""{ "type": "array", "items": { "type": "string", "maxLength": 1 }, "minItems": 5, "uniqueItems": true }""")]
    [InlineData("""{ "type": "array", "items": { "type": "number", "exclusiveMinimum": 0, "exclusiveMaximum": 1 }, "minItems": 12, "uniqueItems": true }""")]
    [InlineData("""{ "type": "array", "uniqueItems": true, "minItems": 4 }""")]
    [InlineData("""{ "type": "array", "items": { "type": "integer" }, "minItems": 40, "uniqueItems": true }""")]
    [InlineData("""{ "allOf": [{ "contains": { "const": 1 } }, { "contains": { "const": 2 } }] }""")]
    [InlineData("""{ "allOf": [{ "contains": { "minimum": 1 } }, { "contains": { "maximum": 3 } }], "maxItems": 1 }""")]
    [InlineData("""{ "type": "array", "uniqueItems": true, "items": { "type": "integer" }, "contains": { "const": 5 }, "minContains": 1, "minItems": 2 }""")]
    public void Harder_arrays_conform(string schema) => Valid(schema);

    [Theory]
    [InlineData(0, "[]")]
    [InlineData(1, "[0]")]
    [InlineData(3, "[0,0,0]")]
    [InlineData(-1, "[]")]
    public void Array_item_count_is_an_option(int count, string expected) =>
        Assert.Equal(expected, Valid(Z.Array(Z.Integer()), new SampleOptions { ArrayItemCount = count }));

    [Theory]
    [InlineData(0, 2, 2)]
    [InlineData(9, 0, 3)]
    public void Array_item_count_is_bounded(int count, int minimum, int expected)
    {
        ArraySchema<long> schema = Z.Array(Z.Integer()).MaxItems(3);
        schema = minimum > 0 ? schema.MinItems(minimum) : schema;
        JsonElement value = schema.GenerateSample(new SampleOptions { ArrayItemCount = count }).Value;
        Assert.Equal(expected, value.GetArrayLength());
    }

    [Theory]
    [InlineData("""{ "type": "object" }""", "{}")]
    [InlineData("""{ "properties": { "a": { "type": "integer" }, "b": { "type": "string" } }, "required": ["b"] }""", """{"a":0,"b":"string"}""")]
    [InlineData("""{ "required": ["x"] }""", """{"x":{}}""")]
    [InlineData("""{ "required": ["x", "y"], "properties": { "a": { "const": 1 } } }""", """{"a":1,"x":{},"y":{}}""")]
    [InlineData("""{ "patternProperties": { "^n": { "type": "integer" } }, "required": ["name"] }""", """{"name":0}""")]
    [InlineData("""{ "additionalProperties": { "type": "integer" }, "required": ["x"] }""", """{"x":0}""")]
    [InlineData("""{ "type": "object", "maxProperties": 1, "properties": { "a": { "const": 1 }, "b": { "const": 2 } } }""", """{"a":1}""")]
    [InlineData("""{ "properties": { "a": false, "b": { "const": 1 } } }""", """{"b":1}""")]
    public void Objects(string schema, string expected) => Assert.Equal(expected, Valid(schema));

    [Theory]
    [InlineData("""{ "required": ["a"], "properties": { "a": { "const": 1 } }, "dependentRequired": { "a": ["b"] } }""", """{"a":1,"b":{}}""")]
    [InlineData("""{ "properties": { "a": { "const": 1 }, "b": { "const": 2 } }, "dependentRequired": { "a": ["b"] }, "required": ["a"] }""", """{"a":1,"b":2}""")]
    [InlineData("""{ "required": ["a"], "properties": { "a": { "const": 1 } }, "dependentSchemas": { "a": { "required": ["c"], "properties": { "c": { "const": 3 } } } } }""", """{"a":1,"c":3}""")]
    [InlineData("""{ "type": "object", "minProperties": 2, "properties": { "a": { "const": 1 }, "b": { "const": 2 }, "c": { "const": 3 } }, "required": ["a"], "maxProperties": 2 }""", """{"a":1,"b":2}""")]
    public void Dependencies_are_followed(string schema, string expected) => Assert.Equal(expected, Valid(schema));

    [Fact]
    public void Required_only_still_follows_dependencies()
    {
        string schema = """{ "required": ["a"], "properties": { "a": { "const": 1 }, "b": { "const": 2 }, "c": { "const": 3 } }, "dependentRequired": { "a": ["b"] } }""";
        Assert.Equal("""{"a":1,"b":2}""", Valid(schema, new SampleOptions { IncludeOptionalProperties = false }));
    }

    [Theory]
    [InlineData("""{ "type": "object", "minProperties": 2 }""", """{"property1":{},"property2":{}}""")]
    [InlineData("""{ "type": "object", "minProperties": 1, "additionalProperties": { "type": "integer" } }""", """{"property1":0}""")]
    [InlineData("""{ "type": "object", "minProperties": 1, "patternProperties": { "^x-[a-z]$": { "const": true } }, "additionalProperties": false }""", """{"x-a":true}""")]
    [InlineData("""{ "type": "object", "minProperties": 3, "patternProperties": { "^[ab]$": { "const": 1 } } }""", """{"a":1,"b":1,"property1":{}}""")]
    [InlineData("""{ "type": "object", "minProperties": 1, "propertyNames": { "pattern": "^k[0-9]$" } }""", """{"k0":{}}""")]
    [InlineData("""{ "type": "object", "minProperties": 1, "propertyNames": { "enum": ["only"] } }""", """{"only":{}}""")]
    public void Min_properties_are_filled(string schema, string expected) => Assert.Equal(expected, Valid(schema));

    [Fact]
    public void Property_names_are_checked() =>
        Assert.Equal("""{"s":2}""", Valid("""{ "propertyNames": { "maxLength": 1 }, "properties": { "long": { "const": 1 }, "s": { "const": 2 } } }"""));

    [Fact]
    public void Output_only_properties_are_skipped() =>
        Assert.Equal("""{"name":"string"}""", Valid(Schema.Object(SampleJson.Default.WithComputed)));

    [Theory]
    [InlineData("""{ "$defs": { "a": { "const": 1 } }, "$ref": "#/$defs/a" }""", "1")]
    [InlineData("""{ "allOf": [{ "minimum": 5 }, { "maximum": 7 }] }""", "5")]
    [InlineData("""{ "allOf": [{ "type": "number" }, { "type": "integer" }, { "exclusiveMinimum": 1.5 }] }""", "2")]
    [InlineData("""{ "allOf": [{ "multipleOf": 4 }, { "multipleOf": 6 }, { "minimum": 1 }] }""", "12")]
    [InlineData("""{ "allOf": [{ "multipleOf": 0.5 }, { "multipleOf": 0.75 }, { "minimum": 1 }] }""", "1.5")]
    [InlineData("""{ "allOf": [{ "minLength": 2 }, { "maxLength": 2 }, { "type": "string" }] }""", "\"st\"")]
    [InlineData("""{ "allOf": [{ "enum": [1, 2, 3] }, { "enum": [2, 3] }, { "not": { "const": 2 } }] }""", "3")]
    [InlineData("""{ "allOf": [{ "properties": { "a": { "minimum": 1 } }, "required": ["a"] }, { "properties": { "a": { "maximum": 2 }, "b": { "const": 3 } }, "required": ["b"] }] }""", """{"a":1,"b":3}""")]
    [InlineData("""{ "allOf": [{ "type": "array", "items": { "minimum": 2 } }, { "items": { "type": "integer" }, "minItems": 2 }] }""", "[2,2]")]
    [InlineData("""{ "allOf": [{ "prefixItems": [{ "type": "integer" }] }, { "items": { "minimum": 4 }, "minItems": 1 }], "type": "array" }""", "[4]")]
    [InlineData("""{ "type": "object", "allOf": [{ "properties": { "a": { "properties": { "b": { "type": "integer" } }, "required": ["b"] } } }, { "properties": { "a": { "properties": { "c": { "type": "string" } }, "required": ["c"] } } }] }""", """{"a":{"b":0,"c":"string"}}""")]
    [InlineData("""{ "anyOf": [{ "type": "string", "maxLength": 0, "minLength": 1 }, { "type": "integer" }] }""", "0")]
    [InlineData("""{ "anyOf": [{ "minimum": 3 }, { "maximum": 1 }], "type": "integer", "not": { "const": 3 } }""", "4")]
    [InlineData("""{ "type": "integer", "anyOf": [{ "minimum": 3 }, { "type": "string" }] }""", "3")]
    [InlineData("""{ "oneOf": [{ "type": "integer" }, { "minimum": 2 }] }""", "0")]
    [InlineData("""{ "oneOf": [{ "type": "integer", "minimum": 2 }, { "type": "integer", "multipleOf": 2 }] }""", "3")]
    [InlineData("""{ "oneOf": [{ "required": ["a"] }, { "required": ["b"] }], "properties": { "a": { "const": 1 }, "b": { "const": 2 } } }""", """{"a":1}""")]
    [InlineData("""{ "type": "integer", "if": { "minimum": 5 }, "then": { "multipleOf": 7 }, "else": { "maximum": -5 } }""", "7")]
    [InlineData("""{ "if": { "type": "string" }, "then": false, "else": { "type": "integer" } }""", "0")]
    [InlineData("""{ "type": "object", "properties": { "kind": { "enum": ["a", "b"] } }, "required": ["kind"], "if": { "properties": { "kind": { "const": "a" } } }, "then": { "required": ["x"], "properties": { "x": { "const": 1 } } } }""", """{"kind":"a","x":1}""")]
    [InlineData("""{ "not": { "type": "object" } }""", "[]")]
    [InlineData("""{ "not": { "type": ["object", "array"] } }""", "0")]
    [InlineData("""{ "type": "string", "not": { "const": "string" } }""", "\"a\"")]
    [InlineData("""{ "type": "integer", "not": { "enum": [0, 1, 2] } }""", "3")]
    [InlineData("""{ "type": "object", "properties": { "a": { "const": 1 } }, "not": { "required": ["a"] } }""", "{}")]
    [InlineData("""{ "type": "object", "required": ["a"], "properties": { "a": { "type": "integer" } }, "not": { "properties": { "a": { "const": 0 } } } }""", """{"a":1}""")]
    [InlineData("true", "{}")]
    [InlineData("{}", "{}")]
    public void Applicators(string schema, string expected) => Assert.Equal(expected, Valid(schema));

    [Fact]
    public void Discriminated_unions_pick_the_first_case()
    {
        JsonValueSchema schema = Z.DiscriminatedUnion(
            "kind",
            Z.Object().Property("kind", Z.String().Const("circle")).Property("radius", Z.Number()),
            Z.Object().Property("kind", Z.String().Const("square")).Property("side", Z.Number()));

        Assert.Equal("""{"kind":"circle","radius":0}""", Valid(schema));
    }

    [Fact]
    public void Typed_polymorphism_and_unions_pick_the_first_case()
    {
        Assert.Equal("""{"$type":"cat","name":"string"}""", Valid(Schema.Object(TestJson.Default.Animal)));

        Schema<Shape> shape = Schema.Union<Shape>("kind")
            .Case("circle", Schema.Object(TestJson.Default.Circle))
            .Case("square", Schema.Object(TestJson.Default.Square));

        Assert.Equal("""{"kind":"circle","radius":0}""", Valid(shape));
    }

    [Theory]
    [InlineData("""{ "type": ["null", "string"] }""", "\"string\"")]
    [InlineData("""{ "type": ["null", "integer"] }""", "0")]
    [InlineData("""{ "anyOf": [{ "type": "null" }, { "type": "integer" }] }""", "0")]
    [InlineData("""{ "oneOf": [{ "type": "null" }, { "type": "boolean" }] }""", "false")]
    [InlineData("""{ "enum": [null, "a"] }""", "\"a\"")]
    [InlineData("""{ "type": "null" }""", "null")]
    public void Nullable_prefers_the_other_type(string schema, string expected) => Assert.Equal(expected, Valid(schema));

    [Fact]
    public void Fluent_nullable_prefers_the_other_type()
    {
        Assert.Equal("\"string\"", Valid(Z.String().Nullable()));
        Assert.Equal("\"a\"", Valid(Z.String().Const("a").Nullable()));
    }

    [Fact]
    public void A_required_nullable_reference_ends_with_null() =>
        Assert.Equal("""{"self":{"self":{"self":{"self":{"self":null}}}}}""", Valid(Nullable));

    [Fact]
    public void Recursion_stops_at_the_depth() =>
        Assert.Equal("""{"value":0,"children":[{"value":0,"children":[{"value":0,"children":[{"value":0,"children":[{"value":0}]}]}]}]}""", Valid(Tree));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void Recursion_depth_is_an_option(int depth)
    {
        string sample = Valid(Tree, new SampleOptions { MaxRecursionDepth = depth });
        Assert.Equal(depth + 2, sample.Split("\"value\"").Length - 1);
    }

    [Theory]
    [InlineData("""{ "$ref": "#/$defs/a", "$defs": { "a": { "properties": { "next": { "$ref": "#/$defs/b" } } }, "b": { "properties": { "back": { "$ref": "#/$defs/a" } } } } }""")]
    [InlineData("""{ "properties": { "self": { "$ref": "#" } } }""")]
    [InlineData("""{ "anyOf": [{ "type": "array", "items": { "$ref": "#" }, "minItems": 1 }, { "type": "integer" }] }""")]
    [InlineData("""{ "type": "object", "required": ["l", "r"], "properties": { "l": { "anyOf": [{ "$ref": "#" }, { "type": "null" }] }, "r": { "anyOf": [{ "$ref": "#" }, { "type": "null" }] } } }""")]
    [InlineData("""{ "type": "array", "items": { "$ref": "#" }, "minItems": 0, "uniqueItems": true }""")]
    public void Cycles_terminate(string schema) => Valid(schema);

    [Fact]
    public void A_product_of_unions_that_all_conflict_ends()
    {
        string unions = string.Join(',', Enumerable.Repeat("""{ "anyOf": [{ "minimum": 1 }, { "minimum": 2 }] }""", 30));
        Unsatisfiable($$"""{ "allOf": [{{unions}}, { "type": "integer" }, { "type": "string" }] }""");
    }

    [Fact]
    public void A_cycle_with_no_value_in_between_terminates() => Assert.Equal("\"string\"", Valid(InPlaceCycle));

    [Fact]
    public void A_wide_recursive_schema_stays_bounded()
    {
        string properties = string.Join(',', Enumerable.Range(0, 30).Select(index => $$"""
            "p{{index}}": { "$ref": "#/$defs/n{{(index + 1) % 10}}" }
            """));
        string definitions = string.Join(',', Enumerable.Range(0, 10).Select(index => $$"""
            "n{{index}}": { "type": "object", "properties": { {{properties}} } }
            """));

        Valid($$"""{ "$defs": { {{definitions}} }, "$ref": "#/$defs/n0" }""");
    }

    [Fact]
    public void Optional_properties_are_an_option()
    {
        ObjectSchema schema = Z.Object().Property("a", Z.Integer()).Property("b", Z.Integer().Optional());
        Assert.Equal("""{"a":0,"b":0}""", Valid(schema));
        Assert.Equal("""{"a":0}""", Valid(schema, new SampleOptions { IncludeOptionalProperties = false }));
    }

    [Fact]
    public void Defaults_of_optional_properties_are_used()
    {
        ObjectSchema schema = Z.Object().Property("limit", Z.Integer().Coerce().Default(10));
        Assert.Equal("""{"limit":10}""", Valid(schema));
    }

    [Fact]
    public void The_same_schema_gives_the_same_sample()
    {
        const string Text = """
            {
              "type": "object",
              "required": ["id", "at", "tags"],
              "properties": {
                "id": { "format": "uuid" },
                "at": { "type": "string", "format": "date-time" },
                "tags": { "type": "array", "items": { "pattern": "^[a-z]{3}$" }, "uniqueItems": true, "minItems": 3 },
                "n": { "type": "number", "exclusiveMinimum": 0.1, "exclusiveMaximum": 0.2 }
              }
            }
            """;

        string first = Z.FromJsonSchema(Text).GenerateSample().Value.GetRawText();
        Assert.Equal(first, Z.FromJsonSchema(Text).GenerateSample().Value.GetRawText());
        Assert.Equal("""{"id":"123e4567-e89b-42d3-a456-000000000000","at":"2024-01-01T12:00:00Z","tags":["aaa","bbb","ccc"],"n":0.15}""", first);
    }

    [Fact]
    public void Typed_schemas_sample_their_properties()
    {
        ObjectSchema<Person> schema = Schema.Object(TestJson.Default.Person)
            .Property(p => p.Name, s => s.MinLength(10))
            .Property(p => p.Age, s => s.Minimum(18).Maximum(65))
            .Property(p => p.Tags, s => s.MinItems(2).UniqueItems());

        SampleResult sample = schema.GenerateSample();
        Assert.True(sample.IsValid, sample.Issues.ToPrettyString());

        Person person = schema.Parse(sample.Value);
        Assert.Equal(10, person.Name.Length);
        Assert.Equal(18, person.Age);
        Assert.Equal(2, person.Tags.Distinct(StringComparer.Ordinal).Count());
        Assert.NotNull(person.Home);
    }

    [Fact]
    public void Typed_recursion_terminates()
    {
        Assert.True(Schema.Object(TestJson.Default.Category).GenerateSample().IsValid);

        string sample = Valid(Schema.Object(TestJson.Default.Node));
        Assert.Equal("""{"id":"string","next":{"id":"string","next":{"id":"string","next":{"id":"string","next":{"id":"string","next":null}}}}}""", sample);
    }

    [Fact]
    public void Typed_dates_and_times_convert()
    {
        ObjectSchema<Event> schema = Schema.Object(SampleJson.Default.Event);
        SampleResult sample = schema.GenerateSample();

        Assert.True(sample.IsValid, $"{sample.Value.GetRawText()}: {sample.Issues.ToPrettyString()}");
        Assert.Equal("string", schema.Parse(sample.Value).Title);
    }

    [Fact]
    public void Refined_typed_dates_convert()
    {
        ObjectSchema<Event> schema = Schema.Object(SampleJson.Default.Event).Refine(item => item.At.Year > 2000, "Too early");
        SampleResult sample = schema.GenerateSample();

        Assert.True(sample.IsValid, $"{sample.Value.GetRawText()}: {sample.Issues.ToPrettyString()}");
    }

    [Fact]
    public void Typed_flags_nullables_and_enums_convert()
    {
        Assert.Equal("""{"access":0,"count":0,"big":0,"color":0}""", Valid(Schema.Object(SampleJson.Default.Flagged)));
        Assert.True(Schema.Object(TestJson.Default.Palette).GenerateSample().IsValid);
        Assert.True(Schema.Object(TestJson.Default.Open).GenerateSample().IsValid);
    }

    [Fact]
    public void A_parsable_schema_with_a_format_converts() =>
        Assert.True(Schema.Parsable<Guid>("uuid").GenerateSample().IsValid);

    [Fact]
    public void A_conversion_that_fails_is_invalid()
    {
        SampleResult sample = Schema.Parsable<Guid>().GenerateSample();
        Assert.False(sample.IsValid);
        Assert.Equal(IssueCodes.DeserializationFailed, sample.Issues[0].Code);
    }

    [Fact]
    public void Number_types_stay_in_range()
    {
        Assert.Equal("255", Valid(Z.Number<byte>().Minimum(255)));
        Assert.False(Z.Number<byte>().Minimum(255).MultipleOf(2).GenerateSample().IsValid);
    }

    [Fact]
    public void Normalized_strings_parse()
    {
        Assert.Equal("\"string\"", Valid(Z.String().Trim()));
        Assert.Equal("\"str\"", Valid(Z.String().Trim().MinLength(1).MaxLength(3)));
        Assert.Equal("\"web\"", Valid(Z.String().ToLowerInvariant().Const("web")));
        Assert.Equal("\"WEB\"", Valid(Z.String().ToUpperInvariant().Enum("WEB")));
        Assert.Equal("\"AB\"", Valid(Z.String().ToUpperInvariant().Pattern("^AB$")));
        Assert.Equal("\"user@example.com\"", Valid(Z.String().Trim().Email()));
        Assert.Equal("\"s\"", Valid(Z.String().Overwrite(_ => "a").MaxLength(1).Refine(text => text == "a", "Not a")));
    }

    [Fact]
    public void Coerced_values_parse()
    {
        Assert.Equal("0", Valid(Z.Integer().Coerce()));
        Assert.Equal("false", Valid(Z.Boolean().Coerce()));
    }

    [Fact]
    public void A_refinement_that_rejects_the_sample_gives_issues()
    {
        SampleResult sample = Z.String().Refine(_ => false, "Never").GenerateSample();

        Assert.False(sample.IsValid);
        Assert.Equal(IssueCodes.Custom, sample.Issues[0].Code);
        Assert.Equal("Never", sample.Issues[0].Message);
    }

    [Fact]
    public void A_refinement_can_steer_to_the_next_candidate() =>
        Assert.Equal("\"a\"", Valid(Z.String().Refine(text => text != "string", "Not that one")));

    [Fact]
    public void A_refinement_on_a_nested_value_gives_issues()
    {
        ObjectSchema schema = Z.Object().Property("a", Z.Integer().Refine(_ => false, "Never"));
        SampleResult sample = schema.GenerateSample();

        Assert.False(sample.IsValid);
        Assert.Equal("/a", sample.Issues[0].InstanceLocation);
    }

    [Fact]
    public void Exceptions_from_a_refinement_pass_through() =>
        Assert.Throws<ArithmeticException>(() => Z.String().Refine(_ => throw new ArithmeticException(), "Unused").GenerateSample());

    [Fact]
    public void Async_refinements_cannot_be_confirmed()
    {
        SampleResult sample = Z.String().RefineAsync((_, _) => ValueTask.FromResult(true), "Unused").GenerateSample();

        Assert.False(sample.IsValid);
        Assert.Equal("\"string\"", sample.Value.GetRawText());
        Assert.Equal(IssueCodes.Custom, sample.Issues[0].Code);
    }

    [Theory]
    [InlineData("""{ "const": "\ud800" }""")]
    [InlineData("""{ "properties": { "a": { "const": "\ud800" } }, "required": ["a"] }""")]
    [InlineData("""{ "type": "array", "items": { "const": "\ud800" }, "minItems": 2 }""")]
    public void Lone_surrogates_in_literals_do_not_throw(string schema) => Z.FromJsonSchema(schema).GenerateSample();

    [Theory]
    [InlineData("""{ "type": "string", "minLength": 2147483647 }""")]
    [InlineData("""{ "type": "array", "minItems": 2147483647 }""")]
    [InlineData("""{ "type": "object", "minProperties": 2147483647 }""")]
    [InlineData("""{ "type": "string", "pattern": "^(a{1000000}){1000000}$" }""")]
    public void Sizes_out_of_reach_are_given_up_on(string schema) => Unsatisfiable(schema);

    [Fact]
    public void Deep_schemas_do_not_overflow_the_stack()
    {
        ISchema schema = Z.Integer();

        for (int level = 0; level < 500; level++)
        {
            schema = Z.Object().Property("a", schema);
        }

        Assert.False(schema.GenerateSample().IsValid);
    }

    [Fact]
    public void A_custom_schema_type_is_validated_too()
    {
        SampleResult sample = new CustomSchema().GenerateSample();
        Assert.True(sample.IsValid);
        Assert.Equal("\"string\"", sample.Value.GetRawText());
    }

    [Fact]
    public void The_guide_shows_this_sample()
    {
        ObjectSchema schema = Z.Object()
            .Property("id", Z.String().Uuid())
            .Property("age", Z.Integer().Minimum(18))
            .Property("tags", Z.Array(Z.String().Pattern("^[a-z]{3}$")).MinItems(2).UniqueItems())
            .Property("nick", Z.String().Optional());

        Assert.Equal("""{"id":"123e4567-e89b-42d3-a456-000000000000","age":18,"tags":["aaa","bbb"],"nick":"string"}""", Valid(schema));
    }

    [Fact]
    public void Huge_min_contains_in_two_places_does_not_overflow()
    {
        SampleResult sample = Z.FromJsonSchema("""
            {"contains":{},"minContains":2147483647,"allOf":[{"contains":{},"minContains":2147483647}]}
            """).GenerateSample();

        Assert.Equal(sample.IsValid, sample.Issues.IsEmpty);
    }

    [Fact]
    public async Task Nested_minimums_stop_at_a_size_budget()
    {
        const string Schema = """
            {"type":"array","minItems":10000,"items":{"type":"array","minItems":10000,"items":{"type":"array","minItems":10000,"items":{"type":"string"}}}}
            """;

        SampleResult sample = await Task.Run(() => Z.FromJsonSchema(Schema).GenerateSample(), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.False(sample.IsValid);
    }

    [Fact]
    public async Task Doubling_required_properties_stop_at_a_size_budget()
    {
        StringBuilder text = new("""{"$ref":"#/$defs/l0","$defs":{""");

        for (int level = 0; level < 30; level++)
        {
            text.Append(CultureInfo.InvariantCulture, $"\"l{level}\":{{\"type\":\"object\",\"required\":[\"x\",\"y\"],\"properties\":{{\"x\":{{\"$ref\":\"#/$defs/l{level + 1}\"}},\"y\":{{\"$ref\":\"#/$defs/l{level + 1}\"}}}}}},");
        }

        text.Append("""
            "l30":{"type":"string"}}}
            """);
        JsonValueSchema schema = Z.FromJsonSchema(text.ToString());

        SampleResult sample = await Task.Run(() => schema.GenerateSample(), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.False(sample.IsValid);
    }

    [Fact]
    public async Task Nested_empty_repeats_with_huge_counts_finish()
    {
        ObjectSchema schema = Z.Object().Property("a", Z.String().Pattern("^((?:){2147483647}){2147483647}$"));

        SampleResult sample = await Task.Run(() => schema.GenerateSample(), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(sample.IsValid, sample.Issues.IsEmpty);
    }

    [Fact]
    public void A_null_schema_throws() => Assert.Throws<ArgumentNullException>(() => SampleExtensions.GenerateSample(null!));

    [Fact]
    public void The_value_outlives_the_generator()
    {
        JsonElement value = Z.Object().Property("a", Z.String()).GenerateSample().Value;
        GC.Collect();
        Assert.Equal("string", value.GetProperty("a").GetString());
    }

    private sealed class CustomSchema : ISchema
    {
        public Model.SchemaNode ToNode() => Z.String().ToNode();
    }
}
