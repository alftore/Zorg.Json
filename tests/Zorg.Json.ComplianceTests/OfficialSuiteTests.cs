using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Xunit;

namespace Zorg.Json.ComplianceTests;

/// <summary>
/// Runs the string cases of the official suite. Its non-string cases expect keywords to ignore other
/// types, which doesn't apply here because <see cref="StringSchema"/> always requires a string.
/// </summary>
public sealed class OfficialSuiteTests
{
    // Needs the ECMA-262 to .NET regex translator: .NET has no \p{Letter}. Remove when it exists.
    private const string UnicodePropertyEscape = "pattern with Unicode property escape requires unicode mode";

    private static void Run(string file, Func<JsonElement, StringSchema> build)
    {
        JsonElement groups = JsonElement.Parse(File.ReadAllText(Path.Combine(TestSuite.DraftPath("draft2020-12"), file)));
        List<string> failures = [];
        int count = 0;

        foreach (JsonElement group in groups.EnumerateArray())
        {
            if (group.GetProperty("description").ValueEquals(UnicodePropertyEscape))
            {
                continue;
            }

            StringSchema schema = build(group.GetProperty("schema"));

            foreach (JsonElement test in group.GetProperty("tests").EnumerateArray())
            {
                JsonElement data = test.GetProperty("data");

                if (data.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                count++;

                if (schema.SafeParse(data).IsSuccess != test.GetProperty("valid").GetBoolean())
                {
                    failures.Add($"{group.GetProperty("description")}: {test.GetProperty("description")}");
                }
            }
        }

        Assert.NotEqual(0, count);
        Assert.Empty(failures);
    }

    [Fact]
    public void MinLength() =>
        Run("minLength.json", schema => Z.String().MinLength((int)schema.GetProperty("minLength").GetDouble()));

    [Fact]
    public void MaxLength() =>
        Run("maxLength.json", schema => Z.String().MaxLength((int)schema.GetProperty("maxLength").GetDouble()));

    [Fact]
    public void Pattern() =>
        Run("pattern.json", schema => Z.String().Pattern(schema.GetProperty("pattern").GetString()!));
}
