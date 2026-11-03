using System.IO;
using Xunit;
using Xunit.Sdk;

namespace Zorg.Json.Tests;

public sealed class SnapshotTests
{
    private const string Schema = """{ "type": "string" }""";

    [Fact]
    public void Matching_text_passes() => Snapshot.Match(Schema);

    [Fact]
    public void Line_endings_are_ignored() =>
        Snapshot.Match($"{Schema}\r\n", testName: nameof(Line_endings_are_ignored));

    [Fact]
    public void Different_text_fails_and_writes_received_file()
    {
        string receivedPath = Path.Combine(Snapshot.RootPath, "SnapshotTests.Matching_text_passes.received.json");

        try
        {
            Assert.Throws<FailException>(() => Snapshot.Match("{}", testName: nameof(Matching_text_passes)));
            Assert.Equal("{}", File.ReadAllText(receivedPath));
        }
        finally
        {
            File.Delete(receivedPath);
        }
    }
}
