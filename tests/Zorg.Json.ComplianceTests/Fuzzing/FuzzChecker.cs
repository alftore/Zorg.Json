#pragma warning disable CA1031 // The harness catches everything on purpose: any exception is a finding.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Schema;
using Zorg.Json.Emission;
using Zorg.Json.Sampling;

namespace Zorg.Json.ComplianceTests.Fuzzing;

/// <summary>Collects findings across threads. A finding is a broken promise, so any one fails the run.</summary>
internal sealed class FuzzFindings
{
    private const int KeptPerKind = 12;

    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _failures = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _notes = new();
    private readonly ConcurrentDictionary<string, int> _counters = new();
    private readonly ConcurrentQueue<(TimeSpan Time, string Detail)> _slow = new();

    public bool Any => !_failures.IsEmpty;

    public void Fail(string kind, string detail)
    {
        ConcurrentQueue<string> queue = _failures.GetOrAdd(kind, _ => new());
        _counters.AddOrUpdate("failure: " + kind, 1, (_, count) => count + 1);

        if (queue.Count < KeptPerKind)
        {
            queue.Enqueue(detail);
        }
    }

    /// <summary>A disagreement that JsonSchema.Net may cause: a different regex dialect, or format as a note. It does not fail a run.</summary>
    public void Note(string kind, string detail)
    {
        ConcurrentQueue<string> queue = _notes.GetOrAdd(kind, _ => new());
        _counters.AddOrUpdate("note: " + kind, 1, (_, count) => count + 1);

        if (queue.Count < KeptPerKind)
        {
            queue.Enqueue(detail);
        }
    }

    public void Count(string name) => _counters.AddOrUpdate(name, 1, (_, count) => count + 1);

    public void Slow(TimeSpan time, string detail)
    {
        if (time > TimeSpan.FromSeconds(1))
        {
            _slow.Enqueue((time, detail));
        }
    }

    public string Describe()
    {
        List<string> lines = [];

        foreach ((string kind, ConcurrentQueue<string> details) in _failures.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            lines.Add($"== {kind} ({_counters.GetValueOrDefault("failure: " + kind)} times)");
            lines.AddRange(details.Select(detail => "  " + detail.ReplaceLineEndings(" ")));
        }

        foreach ((string kind, ConcurrentQueue<string> details) in _notes.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            lines.Add($"-- note: {kind}");
            lines.AddRange(details.Select(detail => "  " + detail.ReplaceLineEndings(" ")));
        }

        lines.Add("== counters");
        lines.AddRange(_counters.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => $"  {entry.Key}: {entry.Value}"));
        lines.Add("== slow iterations");
        lines.AddRange(_slow.OrderByDescending(entry => entry.Time).Take(10).Select(entry => $"  {entry.Time.TotalSeconds:F1}s {entry.Detail.ReplaceLineEndings(" ")}"));
        return string.Join(Environment.NewLine, lines);
    }
}

internal readonly record struct OracleSchema(string Name, JsonSchema? Schema, bool UsesRegex, bool UsesLength, bool UsesFormat, bool Unevaluated, bool UsesMultipleOf, bool UsesBackreference)
{
    /// <summary>Describes what parsing returns, so it judges results rather than documents.</summary>
    public bool IsOutput { get; init; }
}

/// <summary>Runs every check on one schema against a set of documents.</summary>
internal sealed partial class FuzzChecker(FuzzFindings findings)
{
    [GeneratedRegex(@"\\\\[1-9]|\\\\k<")]
    private static partial Regex Backreference();

    public FuzzFindings Findings => findings;

    private static readonly (JsonSchemaProfile Profile, string Name)[] Targets =
    [
        (JsonSchemaProfile.Draft202012, "Draft 2020-12"),
        (JsonSchemaProfile.Draft07, "Draft-07"),
        (JsonSchemaProfile.OpenApi31, "OpenAPI 3.1"),
    ];

    public void Check(Built built, Random random, string origin, bool skipSample = false)
    {
        Stopwatch watch = Stopwatch.StartNew();
        string where = $"{origin} {Truncate(built.Code)}";
        List<OracleSchema> oracles = Emit(built, where);

        List<FuzzDocument> documents = [.. FuzzDocuments.Fixed(), .. Enumerable.Range(0, 12).Select(_ => FuzzDocuments.Random(random)), .. FuzzDocuments.Heavy(random)];

        if (!skipSample)
        {
            Sample(built, oracles, where, random, documents);
        }

        foreach (FuzzDocument document in documents)
        {
            Stopwatch each = Stopwatch.StartNew();
            Parse(built, document, oracles, where);
            findings.Slow(each.Elapsed, $"DOC {document} | {where}");
        }

        watch.Stop();
        findings.Slow(watch.Elapsed, where);
    }

    private static string Truncate(string text) => text.Length > 1500 ? text[..1500] + "..." : text;

    private List<OracleSchema> Emit(Built built, string where)
    {
        List<OracleSchema> oracles = [];

        foreach ((JsonSchemaProfile profile, string name) in Targets.Append((JsonSchemaProfile.OpenApi30, "OpenAPI 3.0")))
        {
            foreach (SchemaDirection direction in Enum.GetValues<SchemaDirection>())
            {
                foreach (UnrepresentablePolicy policy in new[] { UnrepresentablePolicy.Annotate, UnrepresentablePolicy.Omit })
                {
                    try
                    {
                        EmitResult result = built.Probe.Emit(new() { Target = profile, Direction = direction, Unrepresentable = policy });
                        string? text = Serialize(result, name, where);

                        bool output = direction == SchemaDirection.Output && profile == JsonSchemaProfile.Draft202012;

                        if (text is not null && (direction == SchemaDirection.Input || output) && policy == UnrepresentablePolicy.Annotate && profile != JsonSchemaProfile.OpenApi30)
                        {
                            OracleSchema oracle = new(
                                output ? name + " output" : name,
                                Oracle.TryBuild((JsonObject)JsonNode.Parse(text)!),
                                text.Contains("\"pattern\"", StringComparison.Ordinal) || text.Contains("\"patternProperties\"", StringComparison.Ordinal),
                                text.Contains("Length\"", StringComparison.Ordinal),
                                IsImport(built) && text.Contains("\"format\"", StringComparison.Ordinal),
                                IsImport(built) && built.Code.Contains("unevaluated", StringComparison.Ordinal),
                                text.Contains("\"multipleOf\"", StringComparison.Ordinal),
                                Backreference().IsMatch(text));

                            oracles.Add(oracle with { IsOutput = output });
                        }
                    }
                    catch (Exception exception)
                    {
                        findings.Fail("Emit threw", $"{name} {direction} {policy}: {exception.GetType().Name}: {exception.Message} | {where}");
                    }
                }
            }
        }

        EmitReferenceAndThrow(built, where);

        foreach (OracleSchema entry in oracles.Where(entry => entry.Schema is null))
        {
            findings.Count("oracle could not load emitted " + entry.Name);
        }

        return oracles;
    }

    private string? Serialize(EmitResult result, string target, string where)
    {
        try
        {
            return result.Schema.ToJsonString();
        }
        catch (Exception exception)
        {
            findings.Fail("Emitted schema cannot be serialized", $"{target}: {exception.GetType().Name}: {exception.Message} | {where}");
            return null;
        }
    }

    private void EmitReferenceAndThrow(Built built, string where)
    {
        try
        {
            _ = built.Probe.Emit(new() { Reused = ReusedSchemaHandling.Ref, ReferencePrefix = "#/components/schemas/" }).Schema.ToJsonString();
            _ = built.Probe.Emit(new() { Reused = ReusedSchemaHandling.Ref }).Schema.ToJsonString();
            _ = built.Probe.Emit(new() { Unrepresentable = UnrepresentablePolicy.Callback, UnrepresentableHandler = static context => new JsonObject { ["x-note"] = context.Message } }).Schema.ToJsonString();
            _ = built.Probe.Emit(new() { Unrepresentable = UnrepresentablePolicy.Callback, UnrepresentableHandler = static _ => null }).Schema.ToJsonString();
        }
        catch (Exception exception)
        {
            findings.Fail("Emit threw", $"Reused=Ref: {exception.GetType().Name}: {exception.Message} | {where}");
        }

        try
        {
            int diagnostics = built.Probe.Emit(new()).Diagnostics.Length;
            bool threw = false;

            try
            {
                _ = built.Probe.Emit(new() { Unrepresentable = UnrepresentablePolicy.Throw });
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            if (threw != diagnostics > 0)
            {
                findings.Fail("Unrepresentable.Throw disagrees with the diagnostics", $"diagnostics={diagnostics} threw={threw} | {where}");
            }
        }
        catch (Exception exception)
        {
            findings.Fail("Emit threw", $"Throw policy: {exception.GetType().Name}: {exception.Message} | {where}");
        }
    }

    private void Sample(Built built, List<OracleSchema> oracles, string where, Random random, List<FuzzDocument> documents)
    {
        SampleResult sample;

        try
        {
            sample = built.Probe.Sample();
        }
        catch (Exception exception)
        {
            findings.Fail("GenerateSample threw", $"{exception.GetType().Name}: {exception.Message} | {where}");
            return;
        }

        if (!sample.IsValid)
        {
            findings.Count("sample not valid");
            return;
        }

        findings.Count("sample valid");
        string text = sample.Value.GetRawText();
        documents.Add(FuzzDocument.FromText(text));
        documents.AddRange(FuzzDocuments.Mutations(random, text));

        try
        {
            if (!built.Probe.ParseElement(sample.Value))
            {
                findings.Fail("A valid sample does not parse", $"sample {Truncate(text)} | {where}");
            }
        }
        catch (Exception exception)
        {
            findings.Fail("SafeParse threw", $"on sample {Truncate(text)}: {exception.GetType().Name}: {exception.Message} | {where}");
        }

        CheckOracles([.. oracles.Where(entry => !entry.IsOutput)], sample.Value, "A valid sample is rejected by the emitted schema", text, where);
    }

    private void Parse(Built built, FuzzDocument document, List<OracleSchema> oracles, string where)
    {
        bool? viaText = null;
        bool? viaBytes = null;

        try
        {
            if (document.Text is not null)
            {
                viaText = built.Probe.ParseText(document.Text);
            }

            if (document.Utf8 is not null)
            {
                viaBytes = built.Probe.ParseUtf8(document.Utf8);
            }
        }
        catch (Exception exception)
        {
            findings.Fail("SafeParse threw", $"(string or UTF-8 overload) on {document}: {exception.GetType().Name}: {exception.Message} | {where}");
            return;
        }

        try
        {
            if (document.Text is not null && TryElement(document.Text) is { } element)
            {
                if (built.Probe.Revalidate(element) == false)
                {
                    findings.Note("Validate rejects a value that parsing produced", $"on {document} | {where}");
                }
            }
        }
        catch (Exception exception)
        {
            findings.Fail("SafeParse(JsonElement) threw", $"on {document}: {exception.GetType().Name}: {exception.Message} | {where}");
            return;
        }

        if (viaText is { } text && viaBytes is { } bytes && text != bytes)
        {
            findings.Fail("String and UTF-8 overloads disagree", $"on {document}: string={text} utf8={bytes} | {where}");
        }

        if (viaText == true && document.Text is { Length: < 20_000 } raw && TryElement(raw) is { } parsed)
        {
            CheckOracles([.. oracles.Where(entry => !entry.IsOutput)], parsed, "Emitted schema is stricter than parsing", document.ToString(), where);

            // Known limits, such as a OneOf of objects that both match once stripped, make the output schema refuse some
            // results, so a disagreement is a note to read rather than a failure.
            if (built.Probe.ParsedJson(parsed) is { } result)
            {
                // The generator writes defaults that break their own rules, which is the caller's error, so those are counted apart.
                string kind = built.Code.Contains("Default(", StringComparison.Ordinal) || built.Code.Contains("PartialOrExtend", StringComparison.Ordinal)
                    ? "Output schema rejects what parsing returned, with a default"
                    : "Output schema rejects what parsing returned";
                CheckOracles([.. oracles.Where(entry => entry.IsOutput)], result, kind, $"{Truncate(result.GetRawText())} from {document}", where, asNote: true);
            }
        }
    }

    private void CheckOracles(List<OracleSchema> oracles, JsonElement instance, string kind, string document, string where, bool asNote = false)
    {
        bool plainText = document.All(char.IsAscii) && !document.Contains("\\u", StringComparison.Ordinal);

        foreach ((string name, JsonSchema? schema, bool usesRegex, bool usesLength, bool usesFormat, bool unevaluated, bool usesMultipleOf, bool usesBackreference) in oracles)
        {
            if (schema is null)
            {
                continue;
            }

            bool? verdict = Oracle.Accepts(schema, instance, usesMultipleOf);

            if (verdict is null)
            {
                findings.Count(Oracle.IsSafeForOracle(instance.GetRawText(), usesMultipleOf) ? "oracle threw on an instance" : "oracle skipped an unsafe number");
            }
            else if (verdict.Value)
            {
                findings.Count("oracle accepted an instance");
            }
            else
            {
                // Regex and length counting differ only outside plain text, and in backreferences: in ECMA-262 a forward one
                // matches empty, and .NET's fails. Zorg enforces format, so ask again with format enforced.
                if (Oracle.IsBeyondItsPrecision(instance.GetRawText()))
                {
                    findings.Count("oracle rejected a number beyond its precision");
                }
                else if (((usesRegex || usesLength) && !plainText) || usesBackreference || unevaluated)
                {
                    findings.Note(asNote ? kind + ", where the oracle differs" : kind, $"{name} rejects {document} | {where}");
                }
                else if (usesFormat && Oracle.Accepts(schema, instance, usesMultipleOf, assertFormat: true) != false)
                {
                    findings.Count("format enforced on both sides settled a rejection");
                }
                else if (asNote)
                {
                    findings.Note(kind, $"{name} rejects {document} | {where}");
                }
                else
                {
                    findings.Fail(kind, $"{name} rejects {document} | {where}");
                }
            }
        }
    }

    /// <summary>
    /// JsonSchema.Net uses .NET regexes, not ECMA-262, and treats format as a note. Imported schemas keep their own format
    /// keywords in places the fluent API drops them from, so both can disagree without a bug in Zorg.
    /// </summary>
    private static bool IsImport(Built built) => built.Code.StartsWith("import", StringComparison.Ordinal);

    private static JsonElement? TryElement(string text)
    {
        try
        {
            return JsonElement.Parse(text);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
