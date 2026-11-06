using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Xunit;

namespace Zorg.Json.Tests;

public enum Level
{
    Low,
    High,

    [JsonStringEnumMemberName("very-high")]
    VeryHigh,
}

#pragma warning disable CA1069 // Aliases are the point of this test.
public enum Alias
{
    None = 0,
    First = 1,
    Primary = 1,
    Second = 2,
}
#pragma warning restore CA1069

[Flags]
public enum Rights
{
    None = 0,
    Read = 1,
    Write = 2,
}

#pragma warning disable CA1008
public enum Empty;
#pragma warning restore CA1008

public sealed class EnumSchemaTests
{
    [Fact]
    public void It_emits_the_member_names_as_strings()
    {
        JsonObject schema = Z.Enum<Level>().ToJsonSchema();

        Assert.Equal("string", schema["type"]!.GetValue<string>());
        Assert.Equal(["Low", "High", "very-high"], schema["enum"]!.AsArray().Select(name => name!.GetValue<string>()));
    }

    [Fact]
    public void It_parses_a_name_to_the_member()
    {
        Assert.Equal(Level.High, Z.Enum<Level>().Parse("\"High\""));
        Assert.Equal(Level.VeryHigh, Z.Enum<Level>().Parse("\"very-high\""));
    }

    [Fact]
    public void A_member_name_attribute_replaces_the_name()
    {
        Assert.False(Z.Enum<Level>().SafeParse("\"VeryHigh\"").IsSuccess);
    }

    [Fact]
    public void A_naming_policy_changes_the_other_names()
    {
        EnumSchema<Level> schema = Z.Enum<Level>(JsonNamingPolicy.SnakeCaseUpper);

        Assert.Equal(Level.Low, schema.Parse("\"LOW\""));
        Assert.Equal(Level.VeryHigh, schema.Parse("\"very-high\""));
        Assert.False(schema.SafeParse("\"Low\"").IsSuccess);
    }

    [Theory]
    [InlineData("\"high\"")]
    [InlineData("\"HIGH\"")]
    [InlineData("\"Unknown\"")]
    [InlineData("\"\"")]
    [InlineData("\"1\"")]
    public void An_unknown_name_is_an_invalid_value_and_the_case_must_match(string json)
    {
        ParseResult<Level> result = Z.Enum<Level>().SafeParse(json);

        Assert.False(result.IsSuccess);
        Assert.Equal(IssueCodes.InvalidValue, Assert.Single(result.Issues).Code);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("1.0")]
    [InlineData("null")]
    [InlineData("true")]
    public void Anything_but_a_string_is_an_ordinary_issue(string json)
    {
        ParseResult<Level> result = Z.Enum<Level>().SafeParse(json);

        Assert.False(result.IsSuccess);
        Assert.Equal(IssueCodes.InvalidType, Assert.Single(result.Issues).Code);
    }

    [Fact]
    public void Members_that_share_a_value_are_all_accepted()
    {
        EnumSchema<Alias> schema = Z.Enum<Alias>();

        Assert.Equal(Alias.First, schema.Parse("\"First\""));
        Assert.Equal(Alias.First, schema.Parse("\"Primary\""));
        Assert.Equal(4, schema.ToJsonSchema()["enum"]!.AsArray().Count);
    }

    [Fact]
    public void A_flags_enum_takes_one_member_at_a_time()
    {
        EnumSchema<Rights> schema = Z.Enum<Rights>();

        Assert.Equal(Rights.Write, schema.Parse("\"Write\""));
        Assert.Equal(Rights.None, schema.Parse("\"None\""));
        Assert.False(schema.SafeParse("\"Read, Write\"").IsSuccess);
        Assert.False(schema.SafeParse("3").IsSuccess);
    }

    [Fact]
    public void An_enum_without_members_is_refused() =>
        Assert.Throws<ArgumentException>(() => Z.Enum<Empty>());

    [Fact]
    public void Two_members_with_one_name_are_refused() =>
        Assert.Throws<ArgumentException>(() => Z.Enum<Clash>());

    [Fact]
    public void It_works_in_an_object_and_with_a_default()
    {
        ObjectSchema schema = Z.Object().Property("level", Z.Enum<Level>().Default(Level.High));

        Assert.Equal("High", schema.ToJsonSchema()["properties"]!["level"]!["default"]!.GetValue<string>());
        Assert.True(schema.SafeParse("{}").IsSuccess);
        Assert.False(schema.SafeParse("""{"level":2}""").IsSuccess);
    }

    [Fact]
    public void Validate_writes_the_name_back_and_refuses_an_undefined_value()
    {
        EnumSchema<Level> schema = Z.Enum<Level>();

        Assert.Equal(Level.VeryHigh, schema.Validate(Level.VeryHigh).Value);
        Assert.False(schema.Validate((Level)42).IsSuccess);
    }

    [Fact]
    public void The_emitted_schema_is_exact()
    {
        Assert.Empty(Z.Not(Z.Enum<Level>()).Emit().Diagnostics);
    }

    private enum Clash
    {
        A,

        [JsonStringEnumMemberName("A")]
        B,
    }
}
