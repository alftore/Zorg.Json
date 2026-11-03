using System.IO;
using Xunit;

namespace Zorg.Json.ComplianceTests;

/// <summary>Proves the submodule is checked out. Delete once real compliance tests exist.</summary>
public sealed class ScaffoldTests
{
    [Theory]
    [InlineData("draft2020-12")]
    [InlineData("draft7")]
    [InlineData("draft6")]
    public void Test_suite_contains_draft(string draft) =>
        Assert.NotEmpty(Directory.GetFiles(TestSuite.DraftPath(draft), "*.json"));
}
