using System.Text.Json;
using Xunit;

namespace Zorg.Json.Extensions.AI.Tests;

public sealed class ErrorReportTests
{
    // Not InlineData: an attribute stores text as UTF-8, which cannot hold a lone surrogate.
    public static TheoryData<string, string> Cases { get; } = new()
    {
        { "plain", "plain" },
        { "", "" },
        { "a\uD800b", "a�b" },
        { "\uDC00", "�" },
        { "end\uD800", "end�" },
        { "pair 😀 ok", "pair 😀 ok" },
        { "😀\uD800", "😀�" },
        { "\uD800😀", "�😀" },
        { "\uDE00\uD83D", "��" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Clean_replaces_only_unpaired_surrogates(string text, string expected)
    {
        Assert.Equal(expected, ErrorReport.Clean(text));
    }

    [Fact]
    public void A_report_with_unpaired_surrogates_in_every_field_can_be_serialized()
    {
        ParseIssue issue = new() { Code = "x\uD800", Message = "m\uD800", InstanceLocation = "/p\uD800" };

        JsonElement report = ErrorReport.Create("top\uD800", [issue]);

        Assert.Equal("top�", report.GetProperty("message").GetString());
        Assert.Equal("/p�", report.GetProperty("issues")[0].GetProperty("path").GetString());
    }
}
