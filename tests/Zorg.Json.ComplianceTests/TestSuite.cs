using System.IO;
using System.Linq;
using System.Reflection;

namespace Zorg.Json.ComplianceTests;

/// <summary>Locates the JSON-Schema-Test-Suite git submodule.</summary>
internal static class TestSuite
{
    public static string RootPath { get; } = typeof(TestSuite).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "JsonSchemaTestSuitePath")
        .Value!;

    public static string DraftPath(string draft) => Path.Combine(RootPath, "tests", draft);
}
