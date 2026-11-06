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
using Zorg.Json.Import;

namespace Zorg.Json.ComplianceTests.Fuzzing;

/// <summary>
/// Tries to break two promises with random schemas and hostile documents: <c>SafeParse</c> never throws, and the emitted
/// input schema never rejects what parsing accepts. The seed is fixed. Set <c>ZORG_FUZZ_MINUTES</c> for a long run,
/// <c>ZORG_FUZZ_SEED</c> for another seed, and <c>ZORG_FUZZ_REPLAY=fluent:42</c> (or <c>import:42</c>) to repeat one iteration.
/// </summary>
public sealed class FuzzTests
{
    private const long DefaultSeed = 20261105;
    private const int FluentIterations = 300;
    private const int ImportIterations = 400;

    private static long Seed => long.TryParse(Environment.GetEnvironmentVariable("ZORG_FUZZ_SEED"), CultureInfo.InvariantCulture, out long seed) ? seed : DefaultSeed;

    private static TimeSpan? LongRun => double.TryParse(Environment.GetEnvironmentVariable("ZORG_FUZZ_MINUTES"), CultureInfo.InvariantCulture, out double minutes) && minutes > 0
        ? TimeSpan.FromMinutes(minutes)
        : null;

    [Fact]
    public void Fluent_schemas_keep_the_promises() => Drive("fluent", FluentIterations, static (iteration, random, checker) =>
    {
        FluentGenerator generator = new(random);
        Built built = generator.Next();
        checker.Check(built, random, $"fluent:{iteration}");
    });

    [Fact]
    public void Imported_documents_keep_the_promises() => Drive("import", ImportIterations, static (iteration, random, checker) =>
    {
        JsonSchemaDocumentGenerator generator = new(random);
        JsonObject document = generator.Root();
        ImportAndCheck(document.ToJsonString(), generator.Dialect, random, checker, $"import:{iteration}");
    });

    /// <summary>
    /// A long run reported this once, at seed 20261105, import iteration 189485: the emitted schema rejected the sample.
    /// It never came back, under replay, the old source, the old global registry or 300,000 parallel runs, and the cause is unknown.
    /// </summary>
    [Fact]
    public void The_document_from_the_unexplained_fuzz_report_keeps_its_sample()
    {
        const string Document = """
            {"x-unknown":1e999,"oneOf":[{}],"$ref":"#/$defs/d0","type":["array","array"],"$schema":"http://json-schema.org/draft-04/schema#",
             "$defs":{"d0":{"const":1,"$defs":{"d0":{}},"required":["a/b","~"],"type":"object","prefixItems":[{"maxItems":0},{"uniqueItems":false}]},"d1":{}}}
            """;
        const string Sample = """{"a/b":{},"~":{}}""";
        JsonValueSchema schema = Z.FromJsonSchema(Document, new JsonSchemaImportOptions { DefaultDialect = JsonSchemaImportDialect.Draft06 });
        using JsonDocument instance = JsonDocument.Parse(Sample);

        Assert.True(schema.SafeParse(Sample).IsSuccess);
        Assert.True(Oracle.Accepts(Oracle.TryBuild(schema.ToJsonSchema())!, instance.RootElement, usesMultipleOf: false));
    }

    [Fact]
    public void Hostile_schema_documents_import_cleanly()
    {
        FuzzFindings findings = new();
        FuzzChecker checker = new(findings);
        Random random = new(unchecked((int)Seed));

        foreach (string document in HostileSchemaDocuments())
        {
            foreach (JsonSchemaImportDialect dialect in Enum.GetValues<JsonSchemaImportDialect>())
            {
                ImportAndCheck(document, dialect, random, checker, "hostile");
            }
        }

        Assert.False(findings.Any, findings.Describe());
    }

    [Fact]
    public void Typed_schemas_keep_the_promises()
    {
        FuzzFindings findings = new();
        FuzzChecker checker = new(findings);
        Random random = new(unchecked((int)Seed));

        foreach (Built built in TypedSchemas())
        {
            checker.Check(built, random, "typed");
        }

        Assert.False(findings.Any, findings.Describe());
    }

    private static IEnumerable<Built> TypedSchemas()
    {
        yield return Built.Of(Schema.Object(ComplianceJson.Default.Item), "Schema.Object(Item)");
        yield return Built.Of(Schema.Object(ComplianceJson.Default.Item).Property(item => item.Name, name => name.MinLength(1).MaxLength(3).Pattern("^[a-z]+$")), "Item + Name rules");
        yield return Built.Of(Schema.Object(ComplianceJson.Default.Item).Property(item => item.Count, count => count.Minimum(0).Maximum(100).MultipleOf(5)), "Item + Count rules");
        yield return Built.Of(Schema.Object(ComplianceJson.Default.Item).Passthrough().Optional(item => item.Name), "Item passthrough");
        yield return Built.Of(Schema.Object(ComplianceJson.Default.Item).Strip().Refine(item => item.Count != 3, "no", item => item.Count), "Item strip + refine");
        yield return Built.Of(Schema.Object(ComplianceJson.Default.Item).Property(item => item.Note, note => note.Trim().MinLength(1)), "Item + Note trim");
        yield return Built.Of(Schema.Object(ComplianceJson.Default.Patch), "Schema.Object(Patch)");
        yield return Built.Of(Schema.Object(ComplianceJson.Default.Patch).Property(patch => patch.Name, name => name.MinLength(1).Trim()).Property(patch => patch.Count, count => count.Minimum(0)), "Patch + Optional rules");
        yield return Built.Of(Schema.Object(ComplianceJson.Default.Patch).Strip().Property(patch => patch.Nick, nick => nick.MaxLength(3)), "Patch strip + Nick rules");
        yield return Built.Of(Schema.Parsable<Guid>("uuid"), "Parsable<Guid>");
        yield return Built.Of(Schema.Parsable<DateOnly>("date"), "Parsable<DateOnly>");
        yield return Built.Of(Schema.Parsable<TimeSpan>(), "Parsable<TimeSpan>");
        yield return Built.Of(Z.Any().Bind(ComplianceJson.Default.Item), "Any.Bind(Item)");
        yield return Built.Of(Z.AnyOf(Z.Object().Passthrough().Property("name", Z.String().MinLength(2)), Z.Null()).Bind(ComplianceJson.Default.Item), "AnyOf.Bind(Item)");
        yield return Built.Of(Z.Number<int>().Minimum(0), "int");
        yield return Built.Of(Z.Number<decimal>().MultipleOf(0.01m), "decimal");
        yield return Built.Of(Z.Number<byte>(), "byte");
        yield return Built.Of(Z.Number<float>(), "float");
        yield return Built.Of(Z.Number<Half>(), "Half");
        yield return Built.Of(Z.Number<ulong>(), "ulong");
        yield return Built.Of(Z.Number<long>().Coerce(), "long coerce");
    }

    private static void ImportAndCheck(string document, JsonSchemaImportDialect dialect, Random random, FuzzChecker checker, string origin)
    {
        JsonValueSchema schema;

        try
        {
            schema = Z.FromJsonSchema(document, new() { DefaultDialect = dialect, Formats = random.Next(4) == 0 ? FormatHandling.ByDialect : FormatHandling.AssertKnown });
        }
        catch (JsonSchemaImportException)
        {
            checker.Findings.Count("import refused cleanly");
            return;
        }
        catch (Exception exception)
        {
            checker.Findings.Fail("FromJsonSchema threw something other than JsonSchemaImportException", $"{exception.GetType().Name}: {exception.Message} | {dialect} {Short(document)}");
            return;
        }

        checker.Findings.Count("import ok");
        checker.Check(Built.Of(schema, $"import[{dialect}] {document}"), random, origin);
    }

    private static string Short(string text) => text.Length > 600 ? text[..600] + "..." : text;

    private static void Drive(string name, int iterations, Action<int, Random, FuzzChecker> body)
    {
        FuzzFindings findings = new();
        FuzzChecker checker = new(findings);
        string? replay = Environment.GetEnvironmentVariable("ZORG_FUZZ_REPLAY");

        if (replay is not null && replay.StartsWith(name + ":", StringComparison.Ordinal))
        {
            int only = int.Parse(replay[(name.Length + 1)..], CultureInfo.InvariantCulture);
            Run(only, body, checker);
        }
        else if (replay is null && LongRun is { } span)
        {
            LongRunLoop(name, span, body, checker, findings);
        }
        else if (replay is null)
        {
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                Run(iteration, body, checker);
            }
        }

        Assert.False(findings.Any, findings.Describe());
    }

    private static void LongRunLoop(string name, TimeSpan span, Action<int, Random, FuzzChecker> body, FuzzChecker checker, FuzzFindings findings)
    {
        Stopwatch clock = Stopwatch.StartNew();
        int next = -1;
        int threads = int.TryParse(Environment.GetEnvironmentVariable("ZORG_FUZZ_THREADS"), CultureInfo.InvariantCulture, out int requested) && requested > 0 ? requested : Math.Max(1, Environment.ProcessorCount / 2);
        string marker = Path.Combine(Path.GetTempPath(), $"zorg-fuzz-{name}-{Environment.ProcessId}");

        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, worker =>
        {
            while (clock.Elapsed < span)
            {
                int iteration = Interlocked.Increment(ref next);
                File.WriteAllText($"{marker}-{worker}.txt", $"seed={Seed} {name}:{iteration}");
                Run(iteration, body, checker);
            }
        });

        findings.Count($"{name} iterations run: {next + 1} (seed {Seed})");
        string report = Path.Combine(Path.GetTempPath(), $"zorg-fuzz-{name}-findings.txt");
        File.WriteAllText(report, findings.Describe());
    }

    private static void Run(int iteration, Action<int, Random, FuzzChecker> body, FuzzChecker checker)
    {
        Random random = new(unchecked((int)(Seed + (iteration * 7919L))));
        Stopwatch watch = Stopwatch.StartNew();

        try
        {
            body(iteration, random, checker);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            checker.Findings.Fail("Generator or harness threw", $"iteration {iteration} seed {Seed}: {exception}");
        }

        checker.Findings.Slow(watch.Elapsed, $"iteration {iteration} seed {Seed}");
    }

    private static IEnumerable<string> HostileSchemaDocuments()
    {
        yield return """{"const":"\ud800"}""";
        yield return """{"enum":["\udc00","𐀀"]}""";
        yield return """{"pattern":"\ud800"}""";
        yield return """{"properties":{"\ud800":{"type":"string"}},"required":["\ud800"]}""";
        yield return """{"type":"string","type":"number"}""";
        yield return """{"properties":{"a":{},"a":{"type":"string"}}}""";
        yield return """{"enum":[1,1.0,1e0,10e-1]}""";
        yield return """{"const":1e999999999}""";
        yield return """{"minimum":1e999999999}""";
        yield return """{"maximum":-1e999999999,"minimum":1e999999999}""";
        yield return """{"multipleOf":1e-999999}""";
        yield return """{"multipleOf":1e999999}""";
        yield return """{"multipleOf":0}""";
        yield return """{"multipleOf":-1}""";
        yield return """{"maxLength":1e100}""";
        yield return """{"minLength":9999999999}""";
        yield return """{"minItems":1e10}""";
        yield return """{"minItems":2147483648}""";
        yield return """{"minProperties":1.5}""";
        yield return """{"maxItems":-0.0}""";
        yield return """{"minLength":"3"}""";
        yield return """{"type":1}""";
        yield return """{"type":[]}""";
        yield return """{"type":[["string"]]}""";
        yield return """{"required":"a"}""";
        yield return """{"required":[1]}""";
        yield return """{"properties":[]}""";
        yield return """{"properties":{"a":1}}""";
        yield return """{"items":1}""";
        yield return """{"allOf":[]}""";
        yield return """{"anyOf":[]}""";
        yield return """{"oneOf":[]}""";
        yield return """{"allOf":{}}""";
        yield return """{"$ref":1}""";
        yield return """{"$ref":"#"}""";
        yield return """{"$ref":"#/$ref"}""";
        yield return """{"$defs":{"a":{"$ref":"#/$defs/b"},"b":{"$ref":"#/$defs/a"}},"$ref":"#/$defs/a"}""";
        yield return """{"$defs":{"a":{"allOf":[{"$ref":"#/$defs/a"}]}},"$ref":"#/$defs/a"}""";
        yield return """{"$defs":{"a":{"not":{"$ref":"#/$defs/a"}}},"$ref":"#/$defs/a"}""";
        yield return """{"$defs":{"a":{"properties":{"a":{"$ref":"#/$defs/a"}}}},"$ref":"#/$defs/a"}""";
        yield return """{"$ref":"http://example.com/schema"}""";
        yield return """{"$id":"http://example.com/a","$ref":"b.json"}""";
        yield return """{"$id":"::","properties":{"a":{"$id":"//x"}}}""";
        yield return """{"$schema":"https://example.com/x","type":"string"}""";
        yield return """{"pattern":"(a+)+$","type":"string"}""";
        yield return """{"pattern":"^(a|aa)+$","type":"string"}""";
        yield return """{"pattern":"\\p{Foo}"}""";
        yield return """{"pattern":"[[:alpha:]]"}""";
        yield return """{"pattern":"(?<=a)b"}""";
        yield return """{"pattern":"a{99999}"}""";
        yield return """{"pattern":"a{1,99999999999}"}""";
        yield return """{"pattern":"\\1(a)"}""";
        yield return """{"patternProperties":{"[":{}}}""";
        yield return """{"format":"regex","type":"string"}""";
        yield return """{"unevaluatedProperties":false,"properties":{"a":{}},"allOf":[{"properties":{"b":{}}}]}""";
        yield return """{"unevaluatedItems":false,"prefixItems":[{}],"contains":{"type":"string"}}""";
        yield return """{"dependentRequired":{"a":["a"]}}""";
        yield return """{"dependencies":{"a":["b"],"b":["a"]}}""";
        yield return """{"if":true,"then":false}""";
        yield return """{"if":{"type":"string"}}""";
        yield return """{"contains":false,"minContains":0}""";
        yield return """{"maxContains":0,"contains":true}""";
        yield return """{"exclusiveMinimum":true}""";
        yield return """{"minimum":1,"exclusiveMinimum":true,"maximum":1,"exclusiveMaximum":true}""";
        yield return """{"items":[{}],"additionalItems":false,"minItems":5}""";
        yield return """{"const":null,"nullable":true,"type":"string"}""";
        yield return """{"enum":[]}""";
        yield return """{"enum":[{"a":[1,2,{"b":null}]}],"type":"object"}""";
        yield return """{"propertyNames":{"pattern":"^a"},"minProperties":1}""";
        yield return """{"$dynamicRef":"#x","$dynamicAnchor":"x"}""";
        yield return """{"$recursiveRef":"#","$recursiveAnchor":true}""";
        yield return """{"x-inert-1":{"$ref":"#/nope"}}""";
        yield return "true";
        yield return "false";
        yield return "null";
        yield return "1";
        yield return "[]";
        yield return "\"string\"";
        yield return "";
        yield return "{";
        yield return "{} {}";
        yield return "﻿{}";
        yield return string.Concat(Enumerable.Repeat("""{"not":""", 40)) + "{}" + new string('}', 40);
        yield return string.Concat(Enumerable.Repeat("""{"allOf":[""", 30)) + "{}" + string.Concat(Enumerable.Repeat("]}", 30));
        yield return "{\"properties\":{" + string.Join(',', Enumerable.Range(0, 300).Select(index => $"\"p{index}\":{{\"type\":\"string\",\"minLength\":{index % 5}}}")) + "}}";
        yield return "{\"required\":[" + string.Join(',', Enumerable.Range(0, 2000).Select(index => $"\"r{index}\"")) + "]}";
        yield return "{\"enum\":[" + string.Join(',', Enumerable.Range(0, 2000)) + "]}";
        yield return "{\"anyOf\":[" + string.Join(',', Enumerable.Repeat("""{"type":"string"}""", 400)) + "]}";
        yield return "{\"oneOf\":[" + string.Join(',', Enumerable.Repeat("true", 400)) + "]}";
    }
}
