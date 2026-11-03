using System;
using System.Collections.Generic;
using Xunit;

namespace Zorg.Json.Tests;

public sealed class ParseResultTests
{
    private static readonly StringSchema Schema = Z.String().MinLength(2);

    [Fact]
    public void Value_throws_on_failure() =>
        Assert.Throws<InvalidOperationException>(() => Schema.SafeParse("\"a\"").Value);

    [Fact]
    public void Default_is_a_failure_with_no_issues()
    {
        ParseResult<string> result = default;

        Assert.False(result.IsSuccess);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Map_converts_success_and_passes_failure_through()
    {
        Assert.Equal(2, Schema.SafeParse("\"ab\"").Map(text => text.Length).Value);
        Assert.Single(Schema.SafeParse("\"a\"").Map(text => text.Length).Issues);
    }

    [Fact]
    public void Bind_continues_success_and_passes_failure_through()
    {
        StringSchema next = Z.String().MaxLength(1);

        Assert.False(Schema.SafeParse("\"ab\"").Bind(text => next.SafeParse($"\"{text}\"")).IsSuccess);
        Assert.Equal(IssueCodes.TooSmall, Assert.Single(Schema.SafeParse("\"a\"").Bind(next.SafeParse).Issues).Code);
    }

    [Fact]
    public void ToString_is_safe_on_failure()
    {
        Assert.Equal("Success: ab", Schema.SafeParse("\"ab\"").ToString());
        Assert.Equal("Failure: 1 issue(s)", Schema.SafeParse("\"a\"").ToString());
    }

    [Fact]
    public void Issues_format_as_dictionary_keyed_by_pointer()
    {
        Dictionary<string, string[]> errors = Z.String().MinLength(5).Pattern("^x").SafeParse("\"a\"").Issues.ToDictionary();

        Assert.Equal(2, Assert.Single(errors).Value.Length);
        Assert.Equal(string.Empty, Assert.Single(errors).Key);
    }

    [Fact]
    public void Issues_format_as_pretty_text() =>
        Snapshot.Match(Schema.SafeParse("\"a\"").Issues.ToPrettyString(), "txt");
}
