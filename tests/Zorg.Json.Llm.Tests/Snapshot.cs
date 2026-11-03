using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace Zorg.Json.Llm.Tests;

/// <summary>
/// Compares text with <c>Snapshots/{TestClass}.{TestMethod}.verified.{extension}</c>.
/// On a mismatch it writes a <c>.received.</c> file next to it and fails the test. To accept
/// the change, rename the received file to <c>.verified.</c>.
/// </summary>
internal static class Snapshot
{
    public static string RootPath { get; } = typeof(Snapshot).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "SnapshotPath")
        .Value!;

    public static void Match(
        string actual,
        string extension = "json",
        [CallerFilePath] string testFile = "",
        [CallerMemberName] string testName = "")
    {
        string name = $"{Path.GetFileNameWithoutExtension(testFile)}.{testName}";
        string verifiedPath = Path.Combine(RootPath, $"{name}.verified.{extension}");
        string receivedPath = Path.Combine(RootPath, $"{name}.received.{extension}");
        string normalized = actual.ReplaceLineEndings("\n");

        if (File.Exists(verifiedPath) && File.ReadAllText(verifiedPath).ReplaceLineEndings("\n") == normalized)
        {
            File.Delete(receivedPath);
            return;
        }

        Directory.CreateDirectory(RootPath);
        File.WriteAllText(receivedPath, normalized);
        Assert.Fail($"Snapshot differs from {verifiedPath}. Compare it with {receivedPath}.");
    }
}
