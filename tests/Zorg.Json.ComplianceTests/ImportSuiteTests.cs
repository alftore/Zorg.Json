using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Zorg.Json.ComplianceTests;

/// <summary>Runs the whole official suite through the importer, with the exclusions in <c>suite-manifest.json</c>.</summary>
public sealed class ImportSuiteTests
{
    public static TheoryData<string> Files { get; } = [.. SuiteRunner.Files()];

    private static FileResult Result(string relative) =>
        SuiteRunner.Results.Single(result => relative == $"{result.Draft}/{result.File}");

    [Theory]
    [MemberData(nameof(Files))]
    public void Verdicts_match_the_suite(string file) =>
        Assert.True(Result(file).Failures.Count == 0, string.Join('\n', Result(file).Failures));

    [Theory]
    [MemberData(nameof(Files))]
    public void Emitting_and_importing_again_keeps_the_schema(string file) =>
        Assert.True(Result(file).RoundTripFailures.Count == 0, string.Join('\n', Result(file).RoundTripFailures));

    [Fact]
    public void Every_exclusion_is_still_needed()
    {
        _ = SuiteRunner.Results;
        Assert.Empty(SuiteRunner.Manifest.Unused);
    }

    [Fact]
    public void Writes_the_pass_counts()
    {
        StringBuilder report = new();

        foreach (IGrouping<string, FileResult> draft in SuiteRunner.Results.GroupBy(result => result.Draft))
        {
            int passed = draft.Sum(result => result.Passed);
            int refused = draft.Sum(result => result.Refused);
            int skipped = draft.Sum(result => result.Skipped);
            int failed = draft.Sum(result => result.Failures.Count);
            int roundTripped = draft.Sum(result => result.RoundTripped);
            int roundTripFailed = draft.Sum(result => result.RoundTripFailures.Count);
            report.AppendLine(FormattableString.Invariant($"{draft.Key}: passed {passed}, refused at import {refused}, excluded {skipped}, failed {failed}; round trip passed {roundTripped}, failed {roundTripFailed}"));
        }

        foreach (FileResult result in SuiteRunner.Results.Where(result => result.Failures.Count + result.RoundTripFailures.Count + result.NowPassing.Count > 0))
        {
            report.AppendLine(FormattableString.Invariant($"== {result.Draft}/{result.File}"));
            IEnumerable<string> lines = result.Failures.Concat(result.RoundTripFailures.Select(line => $"[round trip] {line}")).Concat(result.NowPassing.Select(line => $"[now passing] {line}"));

            foreach (string line in lines)
            {
                report.AppendLine(line);
            }
        }

        foreach (string unused in SuiteRunner.Manifest.Unused)
        {
            report.AppendLine(FormattableString.Invariant($"[unused exclusion] {unused}"));
        }

        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "suite-report.txt"), report.ToString());
        TestContext.Current.SendDiagnosticMessage(report.ToString());
        Assert.NotEqual(0, SuiteRunner.Results.Sum(result => result.Passed));
    }
}
