#pragma warning disable CA1031 // The harness catches everything on purpose: any exception is a finding.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Zorg.Json.Emission;
using Zorg.Json.Import;
using Zorg.Json.Sampling;

namespace Zorg.Json.ComplianceTests.Fuzzing;

/// <summary>
/// Random reference-heavy documents, embedded in fluent schemas and emitted for every profile, with the judges of
/// <see cref="ReferenceChecker"/>. The seed is fixed. Set <c>ZORG_FUZZ_MINUTES</c> for a long run, <c>ZORG_FUZZ_SEED</c> for
/// another seed, <c>ZORG_FUZZ_REPLAY=reference:42</c> to repeat one iteration, <c>ZORG_FUZZ_ITERATIONS</c> for a longer fixed run, and
/// <c>ZORG_FUZZ_DUMP</c> for a file of what each variant emits, to compare two versions of the emitter. A run always writes
/// <c>zorg-fuzz-reference-report.txt</c> to the temp folder, with every failure by kind, profile and shape.
/// </summary>
public sealed class ReferenceFuzzTests
{
    private const long DefaultSeed = 20261106;
    private static int Iterations => int.TryParse(Environment.GetEnvironmentVariable("ZORG_FUZZ_ITERATIONS"), CultureInfo.InvariantCulture, out int count) ? count : 200;

    private static long Seed => long.TryParse(Environment.GetEnvironmentVariable("ZORG_FUZZ_SEED"), CultureInfo.InvariantCulture, out long seed) ? seed : DefaultSeed;

    private static TimeSpan? LongRun => double.TryParse(Environment.GetEnvironmentVariable("ZORG_FUZZ_MINUTES"), CultureInfo.InvariantCulture, out double minutes) && minutes > 0
        ? TimeSpan.FromMinutes(minutes)
        : null;

    [Fact]
    public void References_keep_their_promises_in_every_profile()
    {
        string? replay = Environment.GetEnvironmentVariable("ZORG_FUZZ_REPLAY");
        ReferenceReport report = new(shrink: replay is not null || LongRun is not null);

        if (replay is not null && replay.StartsWith("reference:", StringComparison.Ordinal))
        {
            OnLargeStack(() => Run(int.Parse(replay["reference:".Length..], CultureInfo.InvariantCulture), report));
        }
        else if (replay is null && LongRun is { } span)
        {
            LongRunLoop(span, report);
        }
        else if (replay is null)
        {
            OnLargeStack(() =>
            {
                for (int iteration = 0; iteration < Iterations; iteration++)
                {
                    Run(iteration, report);
                }
            });
        }

        string text = report.Describe();
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "zorg-fuzz-reference-report.txt"), text);
        Assert.DoesNotContain(report.Counters.All, entry => entry.Key.StartsWith("generator or harness", StringComparison.Ordinal));

        if (replay is null && LongRun is null && Seed == DefaultSeed)
        {
            Assert.True(report.Documents > Iterations / 2, text);
            string[] worse = [.. report.Failing
                .Where(failure => failure.Documents > ReferenceBaseline.Accepted.GetValueOrDefault((failure.Kind, failure.Profile)))
                .Select(failure => $"{failure.Kind} {failure.Profile}: {failure.Documents} documents, {ReferenceBaseline.Accepted.GetValueOrDefault((failure.Kind, failure.Profile))} accepted")];

            Assert.True(worse.Length == 0, string.Join(Environment.NewLine, worse) + Environment.NewLine + text);
        }
    }

    private static void LongRunLoop(TimeSpan span, ReferenceReport report)
    {
        Stopwatch clock = Stopwatch.StartNew();
        int next = -1;
        int threads = int.TryParse(Environment.GetEnvironmentVariable("ZORG_FUZZ_THREADS"), CultureInfo.InvariantCulture, out int requested) && requested > 0 ? requested : Math.Max(1, Environment.ProcessorCount / 2);
        string marker = Path.Combine(Path.GetTempPath(), $"zorg-fuzz-reference-{Environment.ProcessId}");

        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, worker => OnLargeStack(() =>
        {
            while (clock.Elapsed < span)
            {
                int iteration = Interlocked.Increment(ref next);
                File.WriteAllText($"{marker}-{worker}.txt", $"seed={Seed} reference:{iteration}");
                Run(iteration, report);
            }
        }));
    }

    /// <summary>An import that doesn't end would overflow an ordinary stack and take the process with it, where a large one reaches the limit on nodes first.</summary>
    private static void OnLargeStack(Action body)
    {
        Thread thread = new(new ThreadStart(body), maxStackSize: 512 * 1024 * 1024);
        thread.Start();
        thread.Join();
    }

    private static void Run(int iteration, ReferenceReport report)
    {
        Random random = new(unchecked((int)(Seed + (iteration * 7919L))));

        try
        {
            RunCore(iteration, random, report);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            report.Counters.Count($"generator or harness threw: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void RunCore(int iteration, Random random, ReferenceReport report)
    {
        ReferenceDocumentGenerator generator = new(random);
        JsonObject document = generator.Root();
        JsonSchemaImportDialect dialect = generator.Dialect;
        JsonObject second = new ReferenceDocumentGenerator(random).Root(dialect);
        string scenario = ReferenceCase.Scenarios[random.Next(ReferenceCase.Scenarios.Length)];

        // A stack overflow ends the process, so a replay shows the document first.
        if (Environment.GetEnvironmentVariable("ZORG_FUZZ_REPLAY") is not null)
        {
            Console.Error.WriteLine($"{dialect} {scenario} {document.ToJsonString()}");
        }

        string secondText = scenario is "two-imports" or "two-named" ? second.ToJsonString() : "{}";

        void Refused(string code)
        {
            report.Counters.Count($"import refused: {code}");

            if (code == JsonSchemaImportCodes.LimitExceeded)
            {
                report.AddRunaway(iteration, document.ToJsonString());
            }
        }

        ReferenceCase? @case = ReferenceCase.Build(scenario, document.ToJsonString(), secondText, dialect, Refused);

        if (@case is null)
        {
            report.Counters.Count("documents refused by import");
            return;
        }

        report.Counters.Count("documents judged");
        report.Counters.Count($"dialect {dialect}");
        report.Counters.Count($"scenario {scenario}");
        List<JsonElement> instances = Instances(@case, document, second, random);
        report.Counters.Count(instances.Any(instance => @case.Probe.ParseElement(instance)) ? "documents with an accepted value" : "documents with no accepted value");

        if (Environment.GetEnvironmentVariable("ZORG_FUZZ_DUMP") is { } dump)
        {
            Dump(dump, iteration, @case, instances);
        }

        foreach (ReferenceVariant variant in ReferenceVariant.All)
        {
            foreach (ReferenceFailure failure in ReferenceChecker.Check(@case, variant, instances, counters: report.Counters).DistinctBy(failure => failure.Kind))
            {
                report.Add(iteration, @case, variant, failure);
            }
        }

        CheckTogether(iteration, document.ToJsonString(), second.ToJsonString(), dialect, report);
    }

    /// <summary>Both documents, and the first again, as three endpoints of one OpenAPI document.</summary>
    private static void CheckTogether(int iteration, string first, string second, JsonSchemaImportDialect dialect, ReferenceReport report)
    {
        if (ReferenceCase.Build("plain", first, second, dialect) is not { } one || ReferenceCase.Build("plain", second, "{}", dialect) is not { } two)
        {
            return;
        }

        JsonSchemaOptions options = new() { Target = JsonSchemaProfile.OpenApi31, Reused = ReusedSchemaHandling.Ref, ReferencePrefix = "#/components/schemas/" };

        try
        {
            // The same names with other content, which is what two endpoints with a $defs of their own look like.
            ReferenceCase? changed = ReferenceCase.Build("plain", first.Replace("\"type\":\"string\"", "\"type\":\"boolean\"", StringComparison.Ordinal).Replace("\"maximum\":5", "\"maximum\":6", StringComparison.Ordinal), "{}", dialect);
            List<EmitResult> results = [one.Probe.Emit(options), two.Probe.Emit(options), one.Probe.Emit(options)];

            if (changed is not null)
            {
                results.Add(changed.Probe.Emit(options));
            }

            foreach ((string kind, string detail) in ReferenceChecker.CheckComponents(results, report.Counters).DistinctBy(entry => entry.Kind))
            {
                report.AddTogether(iteration, kind, detail, first);
            }

            // One named schema in two wrappers is one definition.
            JsonValueSchema named = Z.FromJsonSchema(first, new() { DefaultDialect = dialect, MaxNodes = 5_000 }).Named("X");
            EmitResult left = Z.Object().Property("one", named).Emit(options);
            EmitResult right = Z.Array(named).Emit(options);

            if (!ReferenceChecker.SameDefinitions(left, right))
            {
                report.AddTogether(iteration, "h-wrapper-changes-definitions", "a definition depends on what is around it", first);
            }
        }
        catch (InvalidOperationException exception)
        {
            report.Counters.Count($"stated limit: {exception.Message[..Math.Min(60, exception.Message.Length)]}");
        }
    }

    /// <summary>Writes what each variant emits, and whether it was sound, to compare two versions of the emitter.</summary>
    private static void Dump(string path, int iteration, ReferenceCase @case, List<JsonElement> instances)
    {
        foreach (ReferenceVariant variant in ReferenceVariant.All)
        {
            string emitted;

            try
            {
                EmitResult result = @case.Probe.Emit(variant.Options);
                emitted = result.Schema.ToJsonString() + " DEFS " + string.Join(",", result.Definitions.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => entry.Key + "=" + entry.Value.ToJsonString()));
            }
            catch (InvalidOperationException exception)
            {
                emitted = "LIMIT " + exception.Message;
            }

            bool clean = ReferenceChecker.Check(@case, variant, instances).All(failure => failure.Kind == ReferenceChecker.OracleLimit);
            File.AppendAllText(path, $"{iteration}\t{variant.Profile}/{variant.Name}\t{@case.Scenario}\t{@case.Dialect}\t{clean}\t{@case.Document}\t{emitted}\n");
        }
    }

    private static List<JsonElement> Instances(ReferenceCase @case, JsonObject document, JsonObject second, Random random)
    {
        List<JsonNode?> first = [.. new ReferenceInstances(random, document).Make(10)];
        List<JsonNode?> other = [.. new ReferenceInstances(random, second).Make(10)];
        List<JsonElement> instances = [.. first.Zip(other).Select(pair => @case.Wrap(pair.First, pair.Second)).Select(Element)];

        try
        {
            SampleResult sample = @case.Probe.Sample();

            if (sample.IsValid)
            {
                instances.Add(sample.Value);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            // Sampling isn't what's being judged here.
        }

        return instances;
    }

    private static JsonElement Element(JsonNode? node) => JsonElement.Parse(node?.ToJsonString() ?? "null");
}
