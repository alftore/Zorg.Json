using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;
using Zorg.Json.ComplianceTests.Fuzzing;
using Zorg.Json.Emission;
using Zorg.Json.Import;
using Zorg.Json.Sampling;

namespace Zorg.Json.ComplianceTests;

/// <summary>
/// Compares Zorg with <c>jv</c> on generated documents. The source document is compared both ways, which is a check against
/// the specification itself, and an emitted schema must accept all that parsing accepts.
/// </summary>
public sealed partial class GoValidatorTests
{
    [GeneratedRegex("[eE][+-]?[0-9]{4,}")]
    private static partial Regex HugeExponent();

    private static int Documents => int.TryParse(Environment.GetEnvironmentVariable("ZORG_JV_DOCUMENTS"), CultureInfo.InvariantCulture, out int count) ? count : 300;

    private static int Seed => int.TryParse(Environment.GetEnvironmentVariable("ZORG_JV_SEED"), CultureInfo.InvariantCulture, out int seed) ? seed : 20261106;

    private static readonly JsonSchemaProfile[] Profiles =
        [JsonSchemaProfile.Draft202012, JsonSchemaProfile.Draft07, JsonSchemaProfile.OpenApi31, JsonSchemaProfile.OpenApi30];

    private static string Draft(JsonSchemaImportDialect dialect) => dialect switch
    {
        JsonSchemaImportDialect.Draft04 => "4",
        JsonSchemaImportDialect.Draft06 => "6",
        JsonSchemaImportDialect.Draft07 => "7",
        _ => "2020",
    };

    private static (string Document, JsonSchemaImportDialect Dialect) NextDocument(Random random, int round)
    {
        if (round % 2 == 0)
        {
            JsonSchemaDocumentGenerator generator = new(random);
            JsonObject root = generator.Root();
            return (root.ToJsonString(), generator.Dialect);
        }

        ReferenceDocumentGenerator references = new(random);
        JsonObject document = references.Root();
        return (document.ToJsonString(), references.Dialect);
    }

    private static List<string> Instances(JsonValueSchema schema, Random random)
    {
        List<FuzzDocument> documents = [.. FuzzDocuments.Fixed(), .. Enumerable.Range(0, 12).Select(_ => FuzzDocuments.Random(random))];

        try
        {
            SampleResult sample = schema.GenerateSample();

            if (sample.IsValid)
            {
                string text = sample.Value.GetRawText();
                documents.Add(FuzzDocument.FromText(text));
                documents.AddRange(FuzzDocuments.Mutations(random, text));
            }
        }
        catch (InvalidOperationException)
        {
            // The fuzzers judge this. Here a schema without a sample still has the other instances.
        }

        return [.. documents.Select(document => document.Text).OfType<string>().Where(IsPlainJson).Distinct().Take(60)];
    }

    /// <summary>Text with a lone surrogate or a repeated name reads differently in Go, and neither is an interoperable document.</summary>
    private static bool IsPlainJson(string text)
    {
        // jv crashes on an exponent it can't hold.
        if (text.Length > 4_000 || text.Contains("\\ud", StringComparison.OrdinalIgnoreCase) || text.Any(char.IsSurrogate) || HugeExponent().IsMatch(text))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            return !HasRepeatedName(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasRepeatedName(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().GroupBy(property => property.Name).Any(group => group.Count() > 1)
            || element.EnumerateObject().Any(property => HasRepeatedName(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().Any(HasRepeatedName),
        _ => false,
    };

    private static bool? Parse(JsonValueSchema schema, string instance)
    {
        ParseResult<JsonElement> result = schema.SafeParse(instance);

        // A limit says nothing about the schema.
        return result.Issues.Any(issue => issue.Code is "limit_exceeded" or "too_complex" or "too_deep") ? null : result.IsSuccess;
    }

    private static JsonValueSchema? TryImport(string document, JsonSchemaImportOptions options)
    {
        try
        {
            return Z.FromJsonSchema(document, options);
        }
        catch (JsonSchemaImportException exception)
        {
            Refusals.AddOrUpdate(exception.Code ?? "none", 1, (_, count) => count + 1);
            return null;
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Refusals = new();

    private static void Report(string name, StringBuilder findings, Dictionary<string, int> counts)
    {
        foreach ((string code, int count) in Refusals)
        {
            counts[$"zorg refused as {code}"] = count;
        }

        string text = string.Join("\n", counts.OrderBy(entry => entry.Key).Select(entry => $"{entry.Key}: {entry.Value}")) + "\n\n" + findings;
        File.WriteAllText(Path.Combine(Path.GetTempPath(), $"zorg-jv-{name}.txt"), text);
    }

    private static bool ReadsOldKeywords(string document) => document.Contains("\"dependencies\"", StringComparison.Ordinal) || document.Contains("\"$recursive", StringComparison.Ordinal);

    private static bool HasFloatInteger(string instance) => instance.Contains('.', StringComparison.Ordinal) || instance.Contains('e', StringComparison.OrdinalIgnoreCase);

    private static bool HasConstBesideReference(JsonNode? node) => node switch
    {
        JsonObject schema => (schema.ContainsKey("$ref") && schema.ContainsKey("const")) || schema.Any(entry => HasConstBesideReference(entry.Value)),
        JsonArray list => list.Any(HasConstBesideReference),
        _ => false,
    };

    private static void Add(Dictionary<string, int> counts, string key) => counts[key] = counts.GetValueOrDefault(key) + 1;

    /// <summary>What a document means. <c>format</c> is an annotation from 2019-09 on, and <c>jv</c> asserts it before that.</summary>
    [Fact]
    public void Source_documents_mean_what_the_specification_says()
    {
        Assert.SkipWhen(GoValidator.Executable is null, "Set ZORG_JV to the path of jv.");

        using GoValidator validator = new();
        Random random = new(Seed);
        StringBuilder findings = new();
        Dictionary<string, int> counts = [];

        for (int round = 0; round < Documents; round++)
        {
            (string document, JsonSchemaImportDialect dialect) = NextDocument(random, round);

            if (dialect == JsonSchemaImportDialect.OpenApi30)
            {
                continue;
            }

            // jv applies a const beside $ref in draft-07 and before, where the specification ignores everything beside it.
            if (dialect != JsonSchemaImportDialect.Draft202012 && HasConstBesideReference(JsonNode.Parse(document)))
            {
                Add(counts, "skipped for the const beside $ref bug in jv");
                continue;
            }

            // jv reads dependencies in 2020-12 too, where the specification has no such keyword.
            if (ReadsOldKeywords(document))
            {
                Add(counts, "skipped for dependencies under 2020-12 in jv");
                continue;
            }

            JsonValueSchema? schema = TryImport(document, new JsonSchemaImportOptions { DefaultDialect = dialect });

            if (schema is null)
            {
                Add(counts, "zorg refused");
                continue;
            }

            List<string> instances = Instances(schema, random);
            bool?[]? verdicts = validator.Judge(document, instances, Draft(dialect), out string? refusal);

            if (verdicts is null)
            {
                Add(counts, "jv refused");
                findings.Append(CultureInfo.InvariantCulture, $"REFUSED {dialect}\n{document}\n{refusal}\n\n");
                continue;
            }

            Add(counts, "judged");

            for (int index = 0; index < instances.Count; index++)
            {
                if ((dialect == JsonSchemaImportDialect.Draft04 || document.Contains("draft-04/schema", StringComparison.Ordinal)) && HasFloatInteger(instances[index]))
                {
                    // Draft-04 tells 1.0 from 1, as the official suite says, and jv doesn't.
                    Add(counts, "skipped for the integer of draft-04 in jv");
                }
                else if (verdicts[index] is null)
                {
                    Add(counts, "jv crashed");
                    findings.Append(CultureInfo.InvariantCulture, $"CRASH {dialect}\n{document}\n{instances[index]}\n\n");
                }
                else if (Parse(schema, instances[index]) is { } parsed)
                {
                    Add(counts, verdicts[index] == true ? "instances jv accepted" : "instances jv rejected");

                    if (parsed != verdicts[index])
                    {
                        Add(counts, parsed ? "zorg accepted, jv rejected" : "zorg rejected, jv accepted");
                        findings.Append(CultureInfo.InvariantCulture, $"DISAGREE zorg={parsed} jv={verdicts[index]} {dialect}\n{document}\n{instances[index]}\n\n");
                    }
                }
            }
        }

        Report("source", findings, counts);
        Assert.True(counts.GetValueOrDefault("judged") > Documents / 10, $"only {counts.GetValueOrDefault("judged")} judged");
        Assert.Equal(0, counts.GetValueOrDefault("zorg accepted, jv rejected") + counts.GetValueOrDefault("zorg rejected, jv accepted"));
    }

    /// <summary>
    /// The formats where the two read the same standard. jv has no check for the idn formats, follows Go's URL parser and regex
    /// syntax, and takes <c>@b.c</c> as an email, so those differ for reasons outside Zorg.
    /// </summary>
    private static readonly string[] Formats =
        ["date", "time", "date-time", "duration", "hostname", "ipv4", "ipv6", "uuid", "json-pointer", "relative-json-pointer"];

    private static readonly string[] FormatTexts =
    [
        "", " ", "a", "A-b", "abc.def", "-a.com", "a-.com", "a..b", "example.com", "EXAMPLE.com", "xn--bcher-kva.example", "b\u00fccher.example", "a_b.example",
        "1.2.3.4", "01.2.3.4", "256.1.1.1", "1.2.3", "::1", "::", "1::", "::ffff:1.2.3.4", "1:2:3:4:5:6:7:8", "1:2:3:4:5:6:7:8:9", "fe80::1%eth0", "12345::",
        "2024-02-29", "2023-02-29", "2024-13-01", "2024-1-01", "0000-01-01", "24-01-01",
        "12:00:00Z", "12:00:00", "23:59:60Z", "22:59:60-01:00", "23:59:60+01:00", "12:00:00.123+02:00", "12:00:00z", "24:00:00Z",
        "2024-02-29T12:00:00Z", "2024-02-29t12:00:00z", "2024-02-29 12:00:00Z", "2016-12-31T23:59:60Z", "2024-02-29T12:00:00",
        "P1D", "PT1H", "P1W", "P1Y2M", "P", "PT", "P1Y2W", "PT1.5S", "p1d", "P1DT",
        "a@b.c", "a@b", "@b.c", "a@", "a b@c.d", "\"a b\"@c.d", "a@[1.2.3.4]", "a@[IPv6:::1]", "a..b@c.d", ".a@b.c", "a.@b.c", "\u00e9@b.c", "a@\u00e9.c",
        "http://a", "http://a/b?c#d", "http://a b", "http://[::1]/", "a:b", "//a", "/a", "a", "#", "?", "http://a/%zz", "http://\u00e9.a/", "\\a", "http://a/{b}",
        "2eb8aa08-aa98-11ea-b4aa-73b441d16380", "2EB8AA08-AA98-11EA-B4AA-73B441D16380", "2eb8aa08aa9811eab4aa73b441d16380", "2eb8aa08-aa98-11ea-b4aa-73b441d1638", "00000000-0000-0000-0000-000000000000",
        "/a/b", "/a~0b", "/a~1b", "/a~2b", "/", "a/b", "0", "1/a", "1#", "0#", "01", "-1", "1~", "2/a~1b",
        "^a$", "[", "(", "a{2}", "\\d+", "(?<n>a)", "\\p{L}", "a{", "(?i)a", "[a-", "\\a",
        "{a}", "{", "}", "{a", "a}", "{+a}", "{#a,b}", "{a:3}", "{a*}", "{?a,b}", "{a b}",
    ];

    /// <summary>What each format means, on strings chosen for where implementations differ.</summary>
    [Fact]
    public void Formats_mean_what_the_specification_says()
    {
        Assert.SkipWhen(GoValidator.Executable is null, "Set ZORG_JV to the path of jv.");

        using GoValidator validator = new();
        StringBuilder findings = new();
        Dictionary<string, int> counts = [];
        string[] instances = [.. FormatTexts.Select(text => $"\"{text}\"")];

        foreach (string format in Formats)
        {
            string document = $$"""{"$schema":"https://json-schema.org/draft/2020-12/schema","format":"{{format}}"}""";
            JsonValueSchema schema = Z.FromJsonSchema(document, new JsonSchemaImportOptions { Formats = FormatHandling.AssertKnown });
            bool?[] verdicts = validator.Judge(document, instances, "2020", out string? refusal, assertFormat: true) ?? throw new InvalidOperationException(refusal);

            for (int index = 0; index < instances.Length; index++)
            {
                bool parsed = schema.SafeParse(instances[index]).IsSuccess;
                Add(counts, verdicts[index] is null ? "jv crashed" : "judged");

                if (verdicts[index] is { } verdict && verdict != parsed)
                {
                    Add(counts, $"differ on {format}");
                    findings.Append(CultureInfo.InvariantCulture, $"FORMAT {format} {instances[index]} zorg={parsed} jv={verdict}\n");
                }
            }
        }

        Report("formats", findings, counts);
        Assert.True(findings.Length == 0, findings.ToString());
    }

    /// <summary>The input guarantee, judged by a validator that keeps numbers exact and doesn't assert <c>format</c>.</summary>
    [Fact]
    public void Emitted_schemas_accept_all_that_parsing_accepts()
    {
        Assert.SkipWhen(GoValidator.Executable is null, "Set ZORG_JV to the path of jv.");

        using GoValidator validator = new();
        Random random = new(Seed + 1);
        StringBuilder findings = new();
        Dictionary<string, int> counts = [];

        for (int round = 0; round < Documents; round++)
        {
            (string document, JsonSchemaImportDialect dialect) = NextDocument(random, round);
            JsonValueSchema? schema = TryImport(document, new JsonSchemaImportOptions { DefaultDialect = dialect, Formats = FormatHandling.AssertKnown });

            if (schema is null)
            {
                Add(counts, "zorg refused");
                continue;
            }

            if (ReadsOldKeywords(document))
            {
                Add(counts, "skipped for dependencies under 2020-12 in jv");
                continue;
            }

            string[] accepted = [.. Instances(schema, random).Where(instance => Parse(schema, instance) == true)];

            foreach (JsonSchemaProfile profile in Profiles)
            {
                string text;

                try
                {
                    JsonNode node = schema.ToJsonSchema(new JsonSchemaOptions { Target = profile });
                    text = (profile.Dialect == JsonSchemaDialect.OpenApi30 ? EmissionReviewTests.ConvertOpenApi30(node)! : node).ToJsonString();
                }
                catch (InvalidOperationException)
                {
                    Add(counts, "emit refused");
                    continue;
                }

                string draft = profile.Dialect == JsonSchemaDialect.Draft07 ? "7" : "2020";
                bool?[]? verdicts = validator.Judge(text, accepted, draft, out string? refusal);

                if (verdicts is null)
                {
                    // Zorg doesn't read all of a document, so a schema that isn't valid against its meta-schema can import, and stays invalid.
                    // A definitions keyword nested where 2020-12 doesn't read it stays as text, and jv follows the references in it anyway.
                    bool sourceIsInvalid = validator.Judge(document, [], Draft(dialect), out _) is null || text.Contains("\"definitions\"", StringComparison.Ordinal);
                    Add(counts, sourceIsInvalid ? "source not valid for jv" : "jv refused");

                    if (!sourceIsInvalid)
                    {
                        findings.Append(CultureInfo.InvariantCulture, $"REFUSED {profile.Dialect}\n{document}\n{text}\n{refusal}\n\n");
                    }

                    continue;
                }

                Add(counts, "judged");

                for (int index = 0; index < accepted.Length; index++)
                {
                    Add(counts, verdicts[index] is null ? "jv crashed" : "instances");

                    if (verdicts[index] == false)
                    {
                        Add(counts, "stricter than parsing");
                        findings.Append(CultureInfo.InvariantCulture, $"STRICTER {profile.Dialect} from {dialect}\n{document}\n{text}\n{accepted[index]}\n\n");
                    }
                }
            }
        }

        Report("emitted", findings, counts);
        Assert.True(counts.GetValueOrDefault("judged") > Documents / 10, $"only {counts.GetValueOrDefault("judged")} judged");
        Assert.Equal(0, counts.GetValueOrDefault("stricter than parsing") + counts.GetValueOrDefault("jv refused"));
    }
}
