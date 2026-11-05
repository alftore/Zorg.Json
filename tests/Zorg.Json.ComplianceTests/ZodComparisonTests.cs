using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Zorg.Json.ComplianceTests;

/// <summary>
/// Zorg.Json against real Zod 4, from fixtures that tools/zod-compare writes. No Node is needed to run
/// these. Every disagreement has to be explained by an entry in tools/zod-compare/known-differences.json.
/// </summary>
public sealed class ZodComparisonTests
{
    private static readonly string[] Statuses = ["design", "undocumented-design", "zod-behaviour", "suspected_bug"];

    [Fact]
    public void EveryCatalogueSchemaHasBothSides()
    {
        string[] zod = [.. ZodFixture.Cases.Select(c => c.Name).Order(StringComparer.Ordinal)];
        string[] zorg = [.. ZodCatalogue.Cases.Keys.Order(StringComparer.Ordinal)];

        Assert.Equal(zod, zorg);
        Assert.True(zod.Length >= 80, $"{zod.Length} cases");
        Assert.Equal("4.6.7", ZodFixture.Version);
    }

    [Fact]
    public void BehaviourMatchesZodOrIsListed() => AssertExplained("behaviour");

    [Fact]
    public void ImportingZodJsonSchemaMatchesZodOrIsListed() => AssertExplained("interop-in");

    [Fact]
    public void OpenApi30IsRefusedWithTheDocumentedCode() => AssertExplained("openapi-refusal");

    [Fact]
    public void ImportingAnOpenApi30DocumentGivesZodsVerdictsOrIsListed() => AssertExplained("openapi-misread");

    [Fact]
    public void ZodReadingZorgJsonSchemaMatchesOrIsListed() => AssertExplained("interop-out");

    [Fact]
    public void StringLengthCountsCodePointsInZod4AsWellAsZorg()
    {
        // docs/guide.md says Zod counts UTF-16 units. Zod 4.6.7 counts code points, so this is not a difference.
        ZodDocument twoEmoji = ZodFixture.Documents.Single(document => document.Text == "\"\\ud83d\\ude00\\ud83d\\ude00\"" || document.Text == "\"\U0001F600\U0001F600\"");
        ZodDocument threeEmoji = ZodFixture.Documents.Single(document => document.Text == "\"\U0001F600\U0001F600\U0001F600\"");

        Assert.False(ZodFixture.Cases.Single(c => c.Name == "string-min3").Results[twoEmoji.Index].Accepted);
        Assert.True(ZodFixture.Cases.Single(c => c.Name == "string-length3").Results[threeEmoji.Index].Accepted);
        Assert.False(ZodCatalogue.Cases["string-min3"].Parse(twoEmoji.Text).Accepted);
        Assert.True(ZodCatalogue.Cases["string-length3"].Parse(threeEmoji.Text).Accepted);
    }

    [Fact]
    public void KnownDifferencesAreWellFormedAndAllUsed()
    {
        List<string> problems = [];
        HashSet<string> ids = [];

        foreach (KnownDifference known in ZodFixture.KnownDifferences)
        {
            if (!ids.Add(known.Id))
            {
                problems.Add($"{known.Id}: duplicate id");
            }

            if (string.IsNullOrWhiteSpace(known.Reason) || string.IsNullOrWhiteSpace(known.Link) || string.IsNullOrWhiteSpace(known.Category))
            {
                problems.Add($"{known.Id}: needs a category, a reason and a link");
            }

            if (!Statuses.Contains(known.Status))
            {
                problems.Add($"{known.Id}: status {known.Status} is not one of {string.Join(", ", Statuses)}");
            }

            if (known.Status == "suspected_bug" && string.IsNullOrWhiteSpace(known.Repro))
            {
                problems.Add($"{known.Id}: a suspected bug needs a repro");
            }

            if (known.Cases.IsDefaultOrEmpty || known.Kinds.IsDefaultOrEmpty)
            {
                problems.Add($"{known.Id}: needs cases and kinds");
            }

            if (!ZodAnalysis.Instance.Used.Contains(known.Id))
            {
                problems.Add($"{known.Id}: matches nothing. Remove it, or the behaviour changed and the entry is stale");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void EmittedFixtureIsCurrent()
    {
        string path = ZodFixture.OutputPath("zorg-emitted.json");
        string committed = File.ReadAllText(path).ReplaceLineEndings("\n");

        Assert.True(
            string.Equals(committed, ZodAnalysis.EmittedFixture(), StringComparison.Ordinal),
            "zorg-emitted.json is stale. Regenerate it: see tools/zod-compare/README.md.");
    }

    [Fact]
    public void WriteFixturesWhenAsked()
    {
        string? mode = Environment.GetEnvironmentVariable("ZORG_ZOD_WRITE");

        if (string.IsNullOrEmpty(mode))
        {
            return;
        }

        if (mode == "emitted")
        {
            File.WriteAllText(Path.Combine(ZodFixture.FixturesSourcePath, "zorg-emitted.json"), ZodAnalysis.EmittedFixture());
        }

        if (mode == "results")
        {
            File.WriteAllText(Path.Combine(ZodFixture.ToolPath, "RESULTS.md"), ZodResultsReport.Build(ZodAnalysis.Instance));
        }

        string? dump = Environment.GetEnvironmentVariable("ZORG_ZOD_DIFFS");

        if (!string.IsNullOrEmpty(dump))
        {
            File.WriteAllLines(
                dump,
                ZodAnalysis.Instance.Items.Select(item => JsonSerializer.Serialize(new
                {
                    status = item.IsExplained ? (item.Inherited ? "inherited" : item.Rule!.Id) : "UNLISTED",
                    part = item.Difference.Part,
                    @case = item.Difference.Case,
                    doc = item.Difference.Document?.Index,
                    text = item.Difference.Document?.Text,
                    tags = item.Difference.Document?.Tags.Order(StringComparer.Ordinal),
                    kind = item.Difference.Kind,
                    zod = item.Difference.Zod,
                    zorg = item.Difference.Zorg,
                    target = item.Difference.Target,
                    io = item.Difference.Io,
                    reused = item.Difference.Reused,
                    detail = item.Difference.Detail,
                })));
        }
    }

    private static void AssertExplained(string part)
    {
        Classified[] unexplained = [.. ZodAnalysis.Instance.Items.Where(item => item.Difference.Part == part && !item.IsExplained)];

        if (unexplained.Length == 0)
        {
            return;
        }

        IEnumerable<string> summary = unexplained
            .GroupBy(item => $"{item.Difference.Kind} zod={item.Difference.Zod} zorg={item.Difference.Zorg}")
            .Select(group => $"{group.Count(),5}  {group.Key}  e.g. {group.First().Difference}");

        Assert.Fail(
            $"{unexplained.Length} {part} disagreements are not in known-differences.json:{Environment.NewLine}"
            + string.Join(Environment.NewLine, summary.Take(40)));
    }
}
