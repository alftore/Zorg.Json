using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;
using Zorg.Json.Emission;

namespace Zorg.Json.Tests;

public sealed partial class RegexPatternTests
{
    /// <summary>Valid in .NET, but ECMA-262 has no atomic groups.</summary>
    private static readonly Regex Atomic = new("^(?>a+)b$", RegexOptions.None, TimeSpan.FromSeconds(1));

    [GeneratedRegex("^[a-z]+$")]
    private static partial Regex Lower();

    [GeneratedRegex("^[a-z]+$", RegexOptions.IgnoreCase)]
    private static partial Regex LowerIgnoringCase();

    [Fact]
    public void Validation_uses_the_given_regex()
    {
        StringSchema schema = Z.String().Pattern(Lower());

        Assert.True(schema.SafeParse("\"abc\"").IsSuccess);

        ParseResult<string> result = schema.SafeParse("\"ABC\"");

        Assert.Equal(IssueCodes.InvalidFormat, Assert.Single(result.Issues).Code);
    }

    [Fact]
    public void Validation_keeps_the_options_of_the_regex()
    {
        Assert.True(Z.String().Pattern(LowerIgnoringCase()).SafeParse("\"ABC\"").IsSuccess);
    }

    [Fact]
    public void Emission_writes_the_regex_text_when_it_is_valid_ecma()
    {
        JsonObject schema = Z.String().Pattern(Lower()).ToJsonSchema();

        Assert.Equal("^[a-z]+$", schema["pattern"]!.GetValue<string>());
    }

    [Fact]
    public void Emission_writes_the_given_ecma_pattern()
    {
        EmitResult result = Z.String().Pattern(Atomic, "^a+b$").Emit();

        Assert.Equal("^a+b$", result.Schema["pattern"]!.GetValue<string>());
        Assert.Empty(result.Diagnostics);
        Assert.True(Z.String().Pattern(Atomic, "^a+b$").SafeParse("\"aab\"").IsSuccess);
    }

    [Fact]
    public void A_regex_without_an_ecma_spelling_is_left_out_with_a_diagnostic()
    {
        EmitResult result = Z.String().Pattern(Atomic).Emit();

        Assert.False(result.Schema.ContainsKey("pattern"));
        EmitDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("pattern", diagnostic.Keyword);
        Assert.Contains("(?>a+)", result.Schema["description"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(Z.String().Pattern(Atomic).SafeParse("\"aab\"").IsSuccess);
        Assert.False(Z.String().Pattern(Atomic).SafeParse("\"aa\"").IsSuccess);
    }

    [Fact]
    public void Options_that_change_matching_leave_the_pattern_unstated()
    {
        EmitResult result = Z.String().Pattern(LowerIgnoringCase()).Emit();

        Assert.False(result.Schema.ContainsKey("pattern"));
        Assert.Equal("pattern", Assert.Single(result.Diagnostics).Keyword);
    }

    [Fact]
    public void An_unstated_pattern_follows_the_policy()
    {
        Assert.Throws<InvalidOperationException>(() => Z.String().Pattern(Atomic).ToJsonSchema(new() { Unrepresentable = UnrepresentablePolicy.Throw }));

        EmitResult omitted = Z.String().Pattern(Atomic).Emit(new() { Unrepresentable = UnrepresentablePolicy.Omit });

        Assert.False(omitted.Schema.ContainsKey("description"));
        Assert.Single(omitted.Diagnostics);
    }

    [Fact]
    public void An_invalid_ecma_pattern_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Z.String().Pattern(Lower(), "(?>a)"));
        Assert.Throws<ArgumentNullException>(() => Z.String().Pattern((Regex)null!));
    }

    [Fact]
    public void The_string_overload_replaces_a_regex()
    {
        StringSchema schema = Z.String().Pattern(Atomic).Pattern("^a$");

        Assert.Equal("^a$", schema.ToJsonSchema()["pattern"]!.GetValue<string>());
        Assert.False(schema.SafeParse("\"aab\"").IsSuccess);
    }

    [Fact]
    public void A_given_regex_is_inexact_so_not_is_left_out()
    {
        JsonObject given = Z.Not(Z.String().Pattern(Lower())).ToJsonSchema();
        JsonObject text = Z.Not(Z.String().Pattern("^[a-z]+$")).ToJsonSchema();

        Assert.False(given.ContainsKey("not"));
        Assert.True(text.ContainsKey("not"));
    }

    [Fact]
    public void A_given_regex_turns_oneOf_into_anyOf()
    {
        JsonObject emitted = Z.OneOf(Z.String().Pattern(Lower()), Z.String().MinLength(5)).ToJsonSchema();

        Assert.False(emitted.ContainsKey("oneOf"));
        Assert.Equal(2, emitted["anyOf"]!.AsArray().Count);
        Assert.True(emitted["anyOf"]!.AsArray().All(branch => branch is not null));
    }

    [Fact]
    public void A_timeout_is_an_issue_not_an_exception()
    {
        Regex slow = new("^(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(20));

        ParseResult<string> result = Z.String().Pattern(slow).SafeParse($"\"{new string('a', 60)}!\"");

        Assert.False(result.IsSuccess);
    }
}
