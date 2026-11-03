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
    // These groups use patternProperties on object data, which Z.String().Pattern(...) can't express. The same
    // patterns are covered by the "pattern" groups next to them in the suite.
    private static readonly HashSet<string> PatternPropertiesGroups =
    [
        "patterns always use unicode semantics with patternProperties",
        "\\w in patternProperties matches [A-Za-z0-9_], not unicode letters",
        "patternProperties with ASCII ranges",
        "\\d in patternProperties matches [0-9], not unicode digits",
        "patternProperties with non-ASCII digits",
        "Proper UTF-16 surrogate pair handling: patternProperties",
    ];

    private static void Run(string file, Func<JsonElement, StringSchema> build)
    {
        JsonElement groups = JsonElement.Parse(File.ReadAllText(Path.Combine(TestSuite.DraftPath("draft2020-12"), file)));
        List<string> failures = [];
        int count = 0;

        foreach (JsonElement group in groups.EnumerateArray())
        {
            if (PatternPropertiesGroups.Contains(group.GetProperty("description").GetString()!))
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

    private static StringSchema PatternSchema(JsonElement schema) => Z.String().Pattern(schema.GetProperty("pattern").GetString()!);

    [Fact]
    public void MinLength() =>
        Run("minLength.json", schema => Z.String().MinLength((int)schema.GetProperty("minLength").GetDouble()));

    [Fact]
    public void MaxLength() =>
        Run("maxLength.json", schema => Z.String().MaxLength((int)schema.GetProperty("maxLength").GetDouble()));

    [Fact]
    public void Pattern() => Run("pattern.json", PatternSchema);

    [Fact]
    public void EcmaScriptRegex() => Run(Path.Combine("optional", "ecmascript-regex.json"), PatternSchema);

    [Fact]
    public void NonBmpRegex() => Run(Path.Combine("optional", "non-bmp-regex.json"), PatternSchema);
}
