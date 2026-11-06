using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Json.Schema;
using Xunit;
using Zorg.Json.Import;
using Zorg.Json.Sampling;

namespace Zorg.Json.ComplianceTests;

/// <summary>What generating a sample did for one schema of the official suite.</summary>
internal sealed record SampleOutcome(string Draft, string File, string Group, string Schema, SampleResult? Sample, bool Satisfiable, string? Thrown)
{
    /// <summary>Null when JsonSchema.Net couldn't load the schema or read the sample, or when the sample isn't valid.</summary>
    public bool? AcceptedByOracle { get; init; }

    public bool IsValid => Sample?.IsValid == true;

    /// <summary>The keyword of the first issue, which says what the search couldn't satisfy.</summary>
    public string FirstKeyword => Sample?.Issues.FirstOrDefault()?.Keyword ?? "none";
}

/// <summary>Generates a sample for every schema of the suite that imports, and has JsonSchema.Net check it.</summary>
public sealed class SampleSuiteTests
{
    private static readonly string[] Drafts = ["draft2020-12", "draft7"];

    private static readonly Lazy<ImmutableArray<SampleOutcome>> LazyOutcomes = new(RunAll);

    public static TheoryData<string> Files { get; } = [.. SuiteRunner.Files().Where(file => Drafts.Contains(Draft(file)))];

    private static ImmutableArray<SampleOutcome> Outcomes => LazyOutcomes.Value;

    [Theory]
    [MemberData(nameof(Files))]
    public void Generating_a_sample_never_throws(string file)
    {
        IEnumerable<string> thrown = Outcomes.Where(outcome => Matches(outcome, file) && outcome.Thrown is not null).Select(outcome => $"{outcome.Group}: {outcome.Thrown}");
        Assert.True(!thrown.Any(), string.Join('\n', thrown));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void A_valid_sample_passes_the_independent_validator(string file)
    {
        IEnumerable<string> rejected = Outcomes.Where(outcome => Matches(outcome, file) && outcome.AcceptedByOracle == false)
            .Select(outcome => $"{outcome.Group}: {outcome.Sample!.Value.GetRawText()} against {outcome.Schema}");

        Assert.True(!rejected.Any(), string.Join('\n', rejected));
    }

    [Theory]
    [InlineData("draft2020-12", 99)]
    [InlineData("draft7", 100)]
    public void Most_schemas_get_a_valid_sample(string draft, int minimumPercent)
    {
        SampleOutcome[] satisfiable = [.. Outcomes.Where(outcome => outcome.Draft == draft && outcome.Satisfiable)];
        int valid = satisfiable.Count(outcome => outcome.IsValid);
        Assert.True(valid * 100 >= satisfiable.Length * minimumPercent, $"{valid} of {satisfiable.Length}");
    }

    [Fact]
    public void Writes_the_valid_share()
    {
        StringBuilder report = new();

        foreach (IGrouping<string, SampleOutcome> draft in Outcomes.GroupBy(outcome => outcome.Draft))
        {
            int total = draft.Count();
            int valid = draft.Count(outcome => outcome.IsValid);
            int satisfiable = draft.Count(outcome => outcome.Satisfiable);
            int satisfiableValid = draft.Count(outcome => outcome.Satisfiable && outcome.IsValid);
            int checkedByOracle = draft.Count(outcome => outcome.AcceptedByOracle is not null);

            report.AppendLine(FormattableString.Invariant(
                $"{draft.Key}: {valid} of {total} valid ({valid * 100.0 / total:F1}%); {satisfiableValid} of {satisfiable} where the suite has a valid instance ({satisfiableValid * 100.0 / satisfiable:F1}%); oracle checked {checkedByOracle} of {valid}"));

            foreach (IGrouping<string, SampleOutcome> keyword in draft.Where(outcome => !outcome.IsValid).GroupBy(outcome => $"{(outcome.Satisfiable ? "satisfiable" : "no valid instance in the suite")}, {outcome.FirstKeyword}").OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                report.AppendLine(FormattableString.Invariant($"  {keyword.Key}: {keyword.Count()}"));
            }
        }

        foreach (SampleOutcome outcome in Outcomes.Where(outcome => !outcome.IsValid))
        {
            report.AppendLine(FormattableString.Invariant($"== {(outcome.Satisfiable ? "MISS" : "none")} {outcome.Draft}/{outcome.File} :: {outcome.Group}: {outcome.FirstKeyword}"));
            report.AppendLine(FormattableString.Invariant($"   {outcome.Schema}"));
            report.AppendLine(FormattableString.Invariant($"   -> {outcome.Sample?.Value.GetRawText()}"));
        }

        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "sample-report.txt"), report.ToString());
        TestContext.Current.SendDiagnosticMessage(report.ToString());
        Assert.Contains(Outcomes, outcome => outcome.AcceptedByOracle == true);
    }

    private static string Draft(string file) => file[..file.IndexOf('/', StringComparison.Ordinal)];

    private static bool Matches(SampleOutcome outcome, string file) => file == $"{outcome.Draft}/{outcome.File}";

    private static ImmutableArray<SampleOutcome> RunAll() =>
        [.. SuiteRunner.Files().Where(file => Drafts.Contains(Draft(file))).SelectMany(RunFile)];

    private static IEnumerable<SampleOutcome> RunFile(string relative)
    {
        string draft = Draft(relative);
        string file = relative[(draft.Length + 1)..];
        JsonSchemaImportOptions options = SuiteRunner.ImportOptions(draft, file);
        JsonElement groups = JsonElement.Parse(File.ReadAllText(Path.Combine(TestSuite.DraftPath(draft), file)));

        foreach (JsonElement group in groups.EnumerateArray())
        {
            JsonValueSchema schema;

            try
            {
                schema = Z.FromJsonSchema(group.GetProperty("schema"), options);
            }
            catch (JsonSchemaImportException)
            {
                continue;
            }

            yield return Run(draft, file, group, schema);
        }
    }

    private static SampleOutcome Run(string draft, string file, JsonElement group, JsonValueSchema schema)
    {
        string description = group.GetProperty("description").GetString()!;
        string text = group.GetProperty("schema").GetRawText();

        // A valid instance that Zorg also accepts proves the schema satisfiable, so a failure there is a miss.
        bool satisfiable = group.GetProperty("tests").EnumerateArray().Any(test =>
            test.GetProperty("valid").GetBoolean() && schema.SafeParse(test.GetProperty("data")).IsSuccess);

        SampleResult sample;

        try
        {
            sample = schema.GenerateSample();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new SampleOutcome(draft, file, description, text, null, satisfiable, exception.ToString());
        }

        SampleOutcome outcome = new(draft, file, description, text, sample, satisfiable, null);
        return sample.IsValid ? outcome with { AcceptedByOracle = Oracle(draft, text, sample.Value) } : outcome;
    }

    private static bool? Oracle(string draft, string schema, JsonElement sample)
    {
        BuildOptions options = new()
        {
            SchemaRegistry = new SchemaRegistry(),
            Dialect = draft == "draft7" ? Dialect.Draft07 : Dialect.Draft202012,
        };

        try
        {
            return JsonSchema.FromText(schema, options).Evaluate(sample).IsValid;
        }
        catch (Exception exception) when (exception is JsonSchemaException or RefResolutionException or JsonException or ArgumentException or InvalidOperationException or OverflowException or FormatException)
        {
            return null;
        }
    }
}
