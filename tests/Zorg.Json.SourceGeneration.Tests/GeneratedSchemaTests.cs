using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Xunit;

namespace Zorg.Json.SourceGeneration.Tests;

[GenerateSchema(typeof(TestJson))]
[Description("A person")]
public partial record Person(
    [property: MinLength(2), MaxLength(5), Description("The name")] string Name,
    [property: Range(0, 150)] int Age,
    [property: EmailAddress] string? Email,
    [property: RegularExpression("^[a-z]+$")] string Code,
    [MinLength(1)] IReadOnlyList<string> Tags,
    [Range(0.5, 1.5, MinimumIsExclusive = true)] double Ratio,
    [Range(0, 1000)] byte Small);

[GenerateSchema(typeof(TestJson))]
public partial class Order
{
    [Required]
    public required string Id { get; init; }

    [Length(1, 2)]
    public required List<Line> Lines { get; init; }

    public required Line First { get; init; }

    public Line? Maybe { get; init; }

    [JsonIgnore]
    [MinLength(1)]
    public string Hidden { get; init; } = string.Empty;
}

[GenerateSchema(typeof(TestJson))]
public partial record Line([property: StringLength(3, MinimumLength = 1)] string Sku);

[GenerateSchema(typeof(TestJson))]
public partial record Tree([property: MinLength(1)] string Name, Tree? Left, IReadOnlyList<Tree>? Children);

[GenerateSchema(typeof(TestJson))]
public partial record Ping([property: MinLength(1)] string Name, Pong? Other);

[GenerateSchema(typeof(TestJson))]
public partial record Pong([property: Range(1, 2)] int Value, Ping? Back);

[GenerateSchema(typeof(TestJson))]
#pragma warning disable CA1815 // Equality is beside the point here.
public partial struct Point
{
    [Range(-1, 1)]
    public int X { get; set; }
}

#pragma warning restore CA1815

[JsonSerializable(typeof(Person))]
[JsonSerializable(typeof(Order))]
[JsonSerializable(typeof(Line))]
[JsonSerializable(typeof(Tree))]
[JsonSerializable(typeof(Ping))]
[JsonSerializable(typeof(Pong))]
[JsonSerializable(typeof(Point))]
public partial class TestJson : JsonSerializerContext;

public sealed class GeneratedSchemaTests
{
    private const string GoodPerson = """{"Name":"ann","Age":30,"Email":"a@b.co","Code":"abc","Tags":["x"],"Ratio":1,"Small":200}""";

    [Fact]
    public void A_good_value_passes_and_is_read()
    {
        ParseResult<Person> result = Person.Schema.SafeParse(GoodPerson);

        Assert.True(result.IsSuccess);
        Assert.Equal("ann", result.Value.Name);
    }

    [Theory]
    [InlineData("\"Name\":\"ann\"", "\"Name\":\"a\"", "minLength")]
    [InlineData("\"Name\":\"ann\"", "\"Name\":\"annnnn\"", "maxLength")]
    [InlineData("\"Age\":30", "\"Age\":151", "maximum")]
    [InlineData("\"Age\":30", "\"Age\":-1", "minimum")]
    [InlineData("\"Email\":\"a@b.co\"", "\"Email\":\"nope\"", "format")]
    [InlineData("\"Code\":\"abc\"", "\"Code\":\"ABC\"", "pattern")]
    [InlineData("\"Tags\":[\"x\"]", "\"Tags\":[]", "minItems")]
    [InlineData("\"Ratio\":1", "\"Ratio\":0.5", "exclusiveMinimum")]
    [InlineData("\"Ratio\":1", "\"Ratio\":1.6", "maximum")]
    public void Each_attribute_is_a_rule(string good, string bad, string keyword)
    {
        ParseResult<Person> result = Person.Schema.SafeParse(GoodPerson.Replace(good, bad, StringComparison.Ordinal));

        Assert.False(result.IsSuccess);
        Assert.Contains(keyword, Person.Schema.ToJsonSchema().ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Nullable_stays_nullable()
    {
        Assert.True(Person.Schema.SafeParse(GoodPerson.Replace("\"a@b.co\"", "null", StringComparison.Ordinal)).IsSuccess);
    }

    [Fact]
    public void Descriptions_reach_the_schema()
    {
        JsonObject schema = Person.Schema.ToJsonSchema();

        Assert.Equal("A person", schema["description"]!.GetValue<string>());
        Assert.Equal("The name", schema["properties"]!["Name"]!["description"]!.GetValue<string>());
    }

    [Fact]
    public void A_bound_the_type_can_not_hold_limits_nothing()
    {
        Assert.DoesNotContain("1000", Person.Schema.ToJsonSchema().ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Nested_types_use_their_generated_schema()
    {
        const string Good = """{"Id":"1","Lines":[{"Sku":"a"}],"First":{"Sku":"b"},"Maybe":null}""";

        Assert.True(Order.Schema.SafeParse(Good).IsSuccess);
        Assert.False(Order.Schema.SafeParse(Good.Replace("\"a\"", "\"aaaa\"", StringComparison.Ordinal)).IsSuccess);
        Assert.False(Order.Schema.SafeParse(Good.Replace("\"b\"", "\"\"", StringComparison.Ordinal)).IsSuccess);
        Assert.False(Order.Schema.SafeParse(Good.Replace("\"1\"", "\"\"", StringComparison.Ordinal)).IsSuccess);
        Assert.False(Order.Schema.SafeParse(Good.Replace("[{\"Sku\":\"a\"}]", "[]", StringComparison.Ordinal)).IsSuccess);
        Assert.False(Order.Schema.SafeParse(Good.Replace("null", "{\"Sku\":\"\"}", StringComparison.Ordinal)).IsSuccess);
    }

    [Fact]
    public void A_recursive_type_works()
    {
        Assert.True(Tree.Schema.SafeParse("""{"Name":"a","Left":{"Name":"b","Left":null,"Children":null},"Children":[{"Name":"c","Left":null,"Children":null}]}""").IsSuccess);
        Assert.False(Tree.Schema.SafeParse("""{"Name":"a","Left":{"Name":"","Left":null,"Children":null},"Children":null}""").IsSuccess);
        Assert.False(Tree.Schema.SafeParse("""{"Name":"a","Left":null,"Children":[{"Name":"","Left":null,"Children":null}]}""").IsSuccess);
        Assert.Contains("$ref", Tree.Schema.ToJsonSchema().ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Two_types_that_hold_each_other_work()
    {
        Assert.True(Ping.Schema.SafeParse("""{"Name":"a","Other":{"Value":1,"Back":{"Name":"b","Other":null}}}""").IsSuccess);
        Assert.False(Ping.Schema.SafeParse("""{"Name":"a","Other":{"Value":3,"Back":null}}""").IsSuccess);
        Assert.False(Ping.Schema.SafeParse("""{"Name":"a","Other":{"Value":1,"Back":{"Name":"","Other":null}}}""").IsSuccess);
    }

    [Fact]
    public void A_struct_works()
    {
        Assert.True(Point.Schema.SafeParse("""{"X":0}""").IsSuccess);
        Assert.False(Point.Schema.SafeParse("""{"X":2}""").IsSuccess);
    }

    [Fact]
    public void An_ignored_property_gets_no_rule()
    {
        Assert.DoesNotContain("Hidden", Order.Schema.ToJsonSchema().ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_schema_is_built_once()
    {
        Assert.Same(Person.Schema, Person.Schema);
        Assert.True(Enumerable.Range(0, 3).All(_ => Person.Schema.SafeParse(GoodPerson).IsSuccess));
    }
}
