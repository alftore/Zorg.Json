using System;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace Zorg.Json.Tests;

public sealed class StringSchemaParseTests
{
    private static ParseIssue SingleIssue<T>(ParseResult<T> result)
    {
        Assert.False(result.IsSuccess);
        return Assert.Single(result.Issues);
    }

    [Fact]
    public void Valid_string_is_returned() => Assert.Equal("hello", Z.String().SafeParse("\"hello\"").Value);

    [Theory]
    [InlineData("1", "number")]
    [InlineData("true", "boolean")]
    [InlineData("false", "boolean")]
    [InlineData("null", "null")]
    [InlineData("[]", "array")]
    [InlineData("{}", "object")]
    public void Other_types_are_rejected(string json, string received)
    {
        ParseIssue issue = SingleIssue(Z.String().SafeParse(json));

        Assert.Equal(IssueCodes.InvalidType, issue.Code);
        Assert.Equal("type", issue.Keyword);
        Assert.Equal(string.Empty, issue.InstanceLocation);
        Assert.Contains(received, issue.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"unterminated")]
    [InlineData("{\"a\":1,\"a\":2}")]
    public void Malformed_json_is_an_issue_not_an_exception(string json) =>
        Assert.Equal(IssueCodes.InvalidJson, SingleIssue(Z.String().SafeParse(json)).Code);

    [Fact]
    public void Lone_surrogate_is_an_issue_not_an_exception() =>
        Assert.Equal(IssueCodes.InvalidValue, SingleIssue(Z.String().SafeParse("\"\\ud800\"")).Code);

    [Theory]
    [InlineData("\"ab\"", true)]
    [InlineData("\"a\"", false)]
    public void MinLength_is_inclusive(string json, bool valid)
    {
        ParseResult<string> result = Z.String().MinLength(2).SafeParse(json);

        Assert.Equal(valid, result.IsSuccess);

        if (!valid)
        {
            ParseIssue issue = Assert.Single(result.Issues);
            Assert.Equal(IssueCodes.TooSmall, issue.Code);
            Assert.Equal("minLength", issue.Keyword);
            Assert.Equal(2, issue.Parameters["minimum"]);
        }
    }

    [Theory]
    [InlineData("\"ab\"", true)]
    [InlineData("\"abc\"", false)]
    public void MaxLength_is_inclusive(string json, bool valid)
    {
        ParseResult<string> result = Z.String().MaxLength(2).SafeParse(json);

        Assert.Equal(valid, result.IsSuccess);

        if (!valid)
        {
            Assert.Equal(IssueCodes.TooBig, Assert.Single(result.Issues).Code);
        }
    }

    [Fact]
    public void Length_counts_code_points_not_utf16_units()
    {
        Assert.True(Z.String().MaxLength(1).SafeParse("\"😀\"").IsSuccess);
        Assert.False(Z.String().MinLength(2).SafeParse("\"😀\"").IsSuccess);
    }

    [Fact]
    public void Pattern_is_not_anchored()
    {
        Assert.True(Z.String().Pattern("b").SafeParse("\"abc\"").IsSuccess);

        ParseIssue issue = SingleIssue(Z.String().Pattern("^b").SafeParse("\"abc\""));
        Assert.Equal(IssueCodes.InvalidFormat, issue.Code);
        Assert.Equal("pattern", issue.Keyword);
    }

    [Fact]
    public void Pattern_with_lookahead_falls_back_to_backtracking_engine() =>
        Assert.True(Z.String().Pattern("^(?=a)a$").SafeParse("\"a\"").IsSuccess);

    [Fact]
    public void Invalid_pattern_fails_when_declared() =>
        Assert.Throws<RegexParseException>(() => Z.String().Pattern("("));

    [Theory]
    [InlineData("email", "\"ann@example.com\"", true)]
    [InlineData("email", "\"not an email\"", false)]
    [InlineData("email", "\"Ann <ann@example.com>\"", false)]
    [InlineData("uuid", "\"2eb8aa08-aa98-11ea-b4aa-73b441d16380\"", true)]
    [InlineData("uuid", "\"2eb8aa08aa9811eab4aa73b441d16380\"", false)]
    [InlineData("uri", "\"https://example.com/a?b=c\"", true)]
    [InlineData("uri", "\"/relative\"", false)]
    [InlineData("date-time", "\"2026-11-03T12:00:00Z\"", true)]
    [InlineData("date-time", "\"2026-11-03T12:00:00.5+01:00\"", true)]
    [InlineData("date-time", "\"2026-11-03\"", false)]
    [InlineData("date-time", "\"2026-13-03T12:00:00Z\"", false)]
    [InlineData("date", "\"2026-11-03\"", true)]
    [InlineData("date", "\"2026-02-30\"", false)]
    public void Known_formats_are_enforced(string format, string json, bool valid) =>
        Assert.Equal(valid, Z.String().Format(format).SafeParse(json).IsSuccess);

    [Fact]
    public void Unknown_format_is_only_an_annotation() =>
        Assert.True(Z.String().Format("my-format").SafeParse("\"anything\"").IsSuccess);

    [Fact]
    public void Format_shortcuts_set_the_keyword()
    {
        Assert.Equal("email", Z.String().Email().ToNode().Format);
        Assert.Equal("uuid", Z.String().Uuid().ToNode().Format);
        Assert.Equal("uri", Z.String().Uri().ToNode().Format);
        Assert.Equal("date-time", Z.String().DateTime().ToNode().Format);
        Assert.Equal("date", Z.String().Date().ToNode().Format);
    }

    [Fact]
    public void Const_allows_only_that_value()
    {
        Assert.True(Z.String().Const("a").SafeParse("\"a\"").IsSuccess);
        Assert.Equal(IssueCodes.InvalidValue, SingleIssue(Z.String().Const("a").SafeParse("\"b\"")).Code);
    }

    [Fact]
    public void Enum_allows_only_those_values()
    {
        StringSchema schema = Z.String().Enum("a", "b");

        Assert.True(schema.SafeParse("\"b\"").IsSuccess);

        ParseIssue issue = SingleIssue(schema.SafeParse("\"c\""));
        Assert.Equal(IssueCodes.InvalidValue, issue.Code);
        Assert.Equal("enum", issue.Keyword);
    }

    [Fact]
    public void All_failing_constraints_are_reported()
    {
        ParseResult<string> result = Z.String().MinLength(5).Pattern("^x").SafeParse("\"abc\"");

        Assert.Equal(2, result.Issues.Length);
    }

    [Theory]
    [InlineData("  a  ", "a")]
    [InlineData("a", "a")]
    public void Trim_applies_before_constraints_and_changes_the_value(string input, string expected) =>
        Assert.Equal(expected, Z.String().Trim().MaxLength(1).SafeParse(JsonSerializer.Serialize(input)).Value);

    [Fact]
    public void Trim_makes_blank_input_fail_MinLength() =>
        Assert.Equal(IssueCodes.TooSmall, SingleIssue(Z.String().Trim().MinLength(1).SafeParse("\"   \"")).Code);

    [Fact]
    public void Normalizers_run_in_declared_order() =>
        Assert.Equal("ab!", Z.String().Overwrite(text => text + "! ").Trim().ToLowerInvariant().SafeParse("\"AB\"").Value);

    [Fact]
    public void Other_normalizers_change_the_value()
    {
        Assert.Equal("a ", Z.String().TrimStart().SafeParse("\" a \"").Value);
        Assert.Equal(" a", Z.String().TrimEnd().SafeParse("\" a \"").Value);
        Assert.Equal("AB", Z.String().ToUpperInvariant().SafeParse("\"ab\"").Value);
        Assert.Equal("é", Z.String().NormalizeUnicode().SafeParse("\"e\\u0301\"").Value);
    }

    [Fact]
    public void Refinement_sees_the_normalized_value()
    {
        Assert.True(Z.String().Trim().Refine(text => text == "a", "unused").SafeParse("\" a \"").IsSuccess);
    }

    [Fact]
    public void Failing_refinement_is_a_custom_issue()
    {
        ParseIssue issue = SingleIssue(Z.String().Refine(text => text != "admin", "Reserved").SafeParse("\"admin\""));

        Assert.Equal(IssueCodes.Custom, issue.Code);
        Assert.Equal("Reserved", issue.Message);
        Assert.Null(issue.Keyword);
    }

    [Fact]
    public void Refinement_is_skipped_when_constraints_fail()
    {
        bool called = false;

        _ = Z.String().MinLength(5).Refine(_ => called = true, "unused").SafeParse("\"a\"");

        Assert.False(called);
    }

    [Fact]
    public void All_failing_refinements_are_reported() =>
        Assert.Equal(2, Z.String().Refine(_ => false, "one").Refine(_ => false, "two").SafeParse("\"a\"").Issues.Length);

    [Fact]
    public void Builder_methods_do_not_change_the_original()
    {
        StringSchema original = Z.String();

        _ = original.MinLength(5).Trim().Refine(_ => false, "unused");

        Assert.Null(original.ToNode().MinLength);
        Assert.Empty(original.ToNode().Behavior.Normalizers);
        Assert.Empty(original.ToNode().Behavior.Refinements);
        Assert.True(original.SafeParse("\"a\"").IsSuccess);
    }

    [Fact]
    public void Utf8_and_element_inputs_behave_like_text()
    {
        StringSchema schema = Z.String().MinLength(2);

        Assert.Equal("ab", schema.SafeParse("\"ab\""u8).Value);
        Assert.Equal("ab", schema.SafeParse(JsonElement.Parse("\"ab\"")).Value);
        Assert.False(schema.SafeParse("\"a\""u8).IsSuccess);
        Assert.Equal(IssueCodes.InvalidJson, SingleIssue(schema.SafeParse("{"u8)).Code);
    }

    [Fact]
    public async Task Stream_input_behaves_like_text()
    {
        using MemoryStream valid = new(Encoding.UTF8.GetBytes("\"ab\""));
        using MemoryStream malformed = new(Encoding.UTF8.GetBytes("{"));

        Assert.Equal("ab", (await Z.String().SafeParseAsync(valid, TestContext.Current.CancellationToken)).Value);
        Assert.Equal(IssueCodes.InvalidJson, SingleIssue(await Z.String().SafeParseAsync(malformed, TestContext.Current.CancellationToken)).Code);
    }

    [Fact]
    public void Parse_returns_the_value_or_throws_with_the_issues()
    {
        StringSchema schema = Z.String().MinLength(2);

        Assert.Equal("ab", schema.Parse("\"ab\""));
        Assert.Equal("ab", schema.Parse("\"ab\""u8));
        Assert.Equal("ab", schema.Parse(JsonElement.Parse("\"ab\"")));

        SchemaValidationException exception = Assert.Throws<SchemaValidationException>(() => schema.Parse("\"a\""));
        Assert.Equal(IssueCodes.TooSmall, Assert.Single(exception.Issues).Code);
    }

    [Fact]
    public void TryParse_reports_through_out_parameters()
    {
        StringSchema schema = Z.String().MinLength(2);

        Assert.True(schema.TryParse("\"ab\"", out string? value, out ImmutableArray<ParseIssue> issues));
        Assert.Equal("ab", value);
        Assert.Empty(issues);

        Assert.False(schema.TryParse("\"a\"", out value, out issues));
        Assert.Null(value);
        Assert.Single(issues);
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        StringSchema schema = Z.String();

        Assert.Throws<ArgumentNullException>(() => schema.SafeParse((string)null!));
        Assert.Throws<ArgumentNullException>(() => schema.Pattern(null!));
        Assert.Throws<ArgumentNullException>(() => schema.Const(null!));
        Assert.Throws<ArgumentNullException>(() => schema.Enum("a", null!));
        Assert.Throws<ArgumentNullException>(() => schema.Refine(null!, "message"));
        Assert.Throws<ArgumentOutOfRangeException>(() => schema.MinLength(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => schema.MaxLength(-1));
    }
}
