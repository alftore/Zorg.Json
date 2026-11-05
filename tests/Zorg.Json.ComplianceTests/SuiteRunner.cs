using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using Json.Schema;
using Zorg.Json.Emission;
using Zorg.Json.Import;

namespace Zorg.Json.ComplianceTests;

internal sealed record FileResult(string Draft, string File)
{
    public int Passed { get; set; }

    public int Refused { get; set; }

    public int Skipped { get; set; }

    public int RoundTripped { get; set; }

    public List<string> Failures { get; } = [];

    public List<string> RoundTripFailures { get; } = [];

    public List<string> NowPassing { get; } = [];
}

/// <summary>Runs the official suite through <see cref="Z.FromJsonSchema(JsonElement, JsonSchemaImportOptions?)"/>, once.</summary>
internal static class SuiteRunner
{
    private static readonly (string Name, JsonSchemaImportDialect Dialect)[] Drafts =
    [
        ("draft2020-12", JsonSchemaImportDialect.Draft202012),
        ("draft7", JsonSchemaImportDialect.Draft07),
        ("draft6", JsonSchemaImportDialect.Draft06),
    ];

    private static readonly string[] Optional =
    [
        "bignum.json", "float-overflow.json", "anchor.json", "id.json", "no-schema.json", "refOfUnknownKeyword.json", "unknownKeyword.json",
    ];

    private static readonly Lazy<ImmutableArray<FileResult>> LazyResults = new(RunAll);

    public static SuiteManifest Manifest { get; } = SuiteManifest.Load();

    public static ImmutableArray<FileResult> Results => LazyResults.Value;

    /// <summary>The files to run for a draft, relative to <c>tests</c>, with forward slashes.</summary>
    public static IEnumerable<string> Files()
    {
        foreach ((string name, _) in Drafts)
        {
            string root = TestSuite.DraftPath(name);
            IEnumerable<string> paths = Directory.GetFiles(root, "*.json")
                .Concat(Directory.GetFiles(Path.Combine(root, "optional", "format"), "*.json"))
                .Concat(Optional.Select(file => Path.Combine(root, "optional", file)).Where(File.Exists));

            foreach (string path in paths.Order(StringComparer.Ordinal))
            {
                yield return $"{name}/{Path.GetRelativePath(root, path).Replace('\\', '/')}";
            }
        }
    }

    public static JsonSchemaImportOptions ImportOptions(string draft, string file) => new()
    {
        DefaultDialect = Drafts.Single(entry => entry.Name == draft).Dialect,

        // Without the optional format vocabulary the suite expects format to be an annotation.
        Formats = file == "format.json" ? FormatHandling.AnnotateOnly : FormatHandling.AssertKnown,
    };

    private static ImmutableArray<FileResult> RunAll() => [.. Files().Select(RunFile)];

    private static FileResult RunFile(string relative)
    {
        string draft = relative[..relative.IndexOf('/', StringComparison.Ordinal)];
        string file = relative[(draft.Length + 1)..];
        FileResult result = new(draft, file);
        JsonSchemaImportOptions options = ImportOptions(draft, file);
        JsonElement groups = JsonElement.Parse(File.ReadAllText(Path.Combine(TestSuite.DraftPath(draft), file)));

        foreach (JsonElement group in groups.EnumerateArray())
        {
            RunGroup(result, relative, group, options);
        }

        return result;
    }

    private static void RunGroup(FileResult result, string relative, JsonElement group, JsonSchemaImportOptions options)
    {
        string description = group.GetProperty("description").GetString()!;
        ImmutableArray<JsonElement> tests = [.. group.GetProperty("tests").EnumerateArray()];
        Exclusion? exclusion = Manifest.ForGroup(relative, description);

        if (exclusion?.Kind == ExclusionKind.ImportError)
        {
            ExpectRefusal(result, description, group, tests, options, exclusion.Code!);
            return;
        }

        JsonValueSchema? schema = null;
        string? importFailure = null;

        try
        {
            schema = Z.FromJsonSchema(group.GetProperty("schema"), options);
        }
        catch (JsonSchemaImportException exception)
        {
            importFailure = $"{exception.Code} at {exception.SchemaLocation}: {exception.Message}";
        }

        if (exclusion is not null)
        {
            result.Skipped += tests.Length;

            if (exclusion.Kind == ExclusionKind.Skip || schema is not null)
            {
                NoteIfPassing(result, description, schema, tests, exclusion);
            }

            return;
        }

        if (schema is null)
        {
            result.Failures.Add($"{description}: import failed with {importFailure}");
            return;
        }

        foreach (JsonElement test in tests)
        {
            string name = test.GetProperty("description").GetString()!;

            if (Manifest.ForTest(relative, description, name) is { } single)
            {
                result.Skipped++;
                NoteIfPassing(result, $"{description} :: {name}", schema, [test], single);
            }
            else if (Agrees(schema, test, out string? problem))
            {
                result.Passed++;
            }
            else
            {
                result.Failures.Add($"{description} :: {name}: {problem}");
            }
        }

        RoundTrip(result, relative, description, schema, tests, options);
    }

    private static void ExpectRefusal(FileResult result, string description, JsonElement group, ImmutableArray<JsonElement> tests, JsonSchemaImportOptions options, string code)
    {
        try
        {
            Z.FromJsonSchema(group.GetProperty("schema"), options);
            result.Failures.Add($"{description}: expected {code}, but it imported");
        }
        catch (JsonSchemaImportException exception) when (exception.Code == code)
        {
            result.Refused += tests.Length;
        }
        catch (JsonSchemaImportException exception)
        {
            result.Failures.Add($"{description}: expected {code}, got {exception.Code} at {exception.SchemaLocation}: {exception.Message}");
        }
    }

    private static void NoteIfPassing(FileResult result, string description, JsonValueSchema? schema, IEnumerable<JsonElement> tests, Exclusion exclusion)
    {
        bool passes = schema is not null && tests.All(test => Agrees(schema, test, out _));

        if (passes && exclusion.Kind == ExclusionKind.Skip)
        {
            result.Failures.Add($"{description}: excluded, but it passes");
        }
        else if (passes)
        {
            result.NowPassing.Add(description);
        }
    }

    private static bool Agrees(JsonValueSchema schema, JsonElement test, out string? problem)
    {
        bool expected = test.GetProperty("valid").GetBoolean();
        ParseResult<JsonElement> parsed = schema.SafeParse(test.GetProperty("data"));
        problem = parsed.IsSuccess == expected ? null : $"expected {(expected ? "valid" : "invalid")}, data {test.GetProperty("data").GetRawText()}";
        return problem is null;
    }

    private static void RoundTrip(FileResult result, string relative, string description, JsonValueSchema schema, ImmutableArray<JsonElement> tests, JsonSchemaImportOptions options)
    {
        JsonSchemaProfile[] targets = result.Draft == "draft2020-12"
            ? [JsonSchemaProfile.Draft202012]
            : [JsonSchemaProfile.Draft202012, JsonSchemaProfile.Draft07];

        foreach (JsonSchemaProfile target in targets)
        {
            JsonSchemaOptions emit = new() { Target = target };
            string prefix = $"{description} [{target.Name}]";

            try
            {
                string first = schema.ToJsonSchema(emit).ToJsonString();
                JsonValueSchema again = Z.FromJsonSchema(first, options);
                string second = again.ToJsonSchema(emit).ToJsonString();

                if (first != second)
                {
                    result.RoundTripFailures.Add($"{prefix}: not idempotent\n  {first}\n  {second}");
                }

                JsonSchema meta = target == JsonSchemaProfile.Draft07 ? MetaSchemas.Draft7 : MetaSchemas.Draft202012;

                if (!meta.Evaluate(JsonElement.Parse(first)).IsValid)
                {
                    result.RoundTripFailures.Add($"{prefix}: invalid against the meta-schema\n  {first}");
                }

                foreach (JsonElement test in tests)
                {
                    string name = test.GetProperty("description").GetString()!;

                    if (Manifest.ForTest(relative, description, name) is null)
                    {
                        if (Agrees(again, test, out string? problem))
                        {
                            result.RoundTripped++;
                        }
                        else
                        {
                            result.RoundTripFailures.Add($"{prefix} :: {name}: {problem}\n  {first}");
                        }
                    }
                }
            }
            catch (JsonSchemaImportException exception)
            {
                result.RoundTripFailures.Add($"{prefix}: re-import failed with {exception.Code} at {exception.SchemaLocation}: {exception.Message}");
            }
        }
    }
}
