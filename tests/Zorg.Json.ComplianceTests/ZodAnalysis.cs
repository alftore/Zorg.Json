using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zorg.Json.Emission;
using Zorg.Json.Import;

namespace Zorg.Json.ComplianceTests;

/// <summary>A disagreement, and what explains it: a listed rule, an earlier disagreement it follows from, or nothing.</summary>
internal sealed record Classified(Difference Difference, KnownDifference? Rule, bool Inherited)
{
    public bool IsExplained => Rule is not null || Inherited;
}

/// <summary>Runs the whole comparison once. Every test reads from this.</summary>
internal sealed class ZodAnalysis
{
    public static readonly string[] ImportTargets = ["draft-2020-12", "draft-07", "draft-04"];

    public static readonly string[] EmitTargets = ["draft-2020-12", "draft-07"];

    private static readonly Lazy<ZodAnalysis> Lazy = new(() => new ZodAnalysis());

    private readonly List<Difference> _behaviour = [];
    private readonly List<(Difference Difference, bool Inheritable)> _other = [];

    private ZodAnalysis()
    {
        foreach (ZodCaseData zodCase in ZodFixture.Cases)
        {
            if (!ZodCatalogue.Cases.TryGetValue(zodCase.Name, out ZorgCase? zorgCase))
            {
                continue;
            }

            Outcomes[zodCase.Name] = [.. ZodFixture.Documents.Select(document => zorgCase.Parse(document.Text))];
            CompareBehaviour(zodCase);
        }

        HashSet<(string, int)> verdictDifferences = [.. _behaviour.Where(d => d.Kind == "verdict").Select(d => (d.Case, d.Document!.Index))];

        foreach (ZodCaseData zodCase in ZodFixture.Cases.Where(c => ZodCatalogue.Cases.ContainsKey(c.Name)))
        {
            CompareImport(zodCase, verdictDifferences);
            CompareOpenApi(zodCase, verdictDifferences);
        }

        CompareZodReader(verdictDifferences);

        Items =
        [
            .. _behaviour.Select(Classify),
            .. _other.Select(item => item.Inheritable ? new Classified(item.Difference, null, true) : Classify(item.Difference)),
        ];

        Used = [.. Items.Where(item => item.Rule is not null).Select(item => item.Rule!.Id).Distinct()];
    }

    public static ZodAnalysis Instance => Lazy.Value;

    public Dictionary<string, ImmutableArray<ZodOutcome>> Outcomes { get; } = [];

    public ImmutableArray<Classified> Items { get; }

    public ImmutableHashSet<string> Used { get; }

    /// <summary>Comparisons made and kept, by a label such as <c>behaviour</c> or <c>import draft-07 input</c>.</summary>
    public Dictionary<string, int> Compared { get; } = [];

    public Dictionary<string, int> Refused { get; } = [];

    private static string Key(params string[] parts) => string.Join(' ', parts);

    private static Classified Classify(Difference difference)
    {
        KnownDifference? rule = ZodFixture.KnownDifferences.FirstOrDefault(known => known.Matches(difference));
        return new Classified(difference, rule, false);
    }

    private void Count(string key, int by = 1) => Compared[key] = Compared.GetValueOrDefault(key) + by;

    private void CompareBehaviour(ZodCaseData zodCase)
    {
        ImmutableArray<ZodOutcome> outcomes = Outcomes[zodCase.Name];

        for (int index = 0; index < ZodFixture.Documents.Length; index++)
        {
            ZodDocument document = ZodFixture.Documents[index];
            Count("behaviour");
            (string Kind, string Detail)? difference = ZodComparer.Compare(zodCase.Results[index], outcomes[index], document.Text);

            if (difference is { } found)
            {
                _behaviour.Add(new Difference(
                    "behaviour",
                    zodCase.Name,
                    document,
                    found.Kind,
                    ZodComparer.State(zodCase.Results[index].Accepted),
                    ZodComparer.State(outcomes[index].Accepted),
                    Detail: found.Detail));
            }
        }
    }

    private void CompareImport(ZodCaseData zodCase, HashSet<(string, int)> verdictDifferences)
    {
        foreach (string target in ImportTargets)
        {
            foreach (string io in new[] { "input", "output" })
            {
                JsonElement entry = zodCase.Schemas.GetProperty(target).GetProperty(io);

                foreach (string reused in new[] { "inline", "ref" })
                {
                    JsonElement schemaEntry = entry.GetProperty(reused);

                    if (schemaEntry.ValueKind == JsonValueKind.String)
                    {
                        continue;
                    }

                    string label = Key("import", target, io, reused);

                    if (schemaEntry.TryGetProperty("error", out _))
                    {
                        Refused[Key("zod-unrepresentable", target, io, reused)] = Refused.GetValueOrDefault(Key("zod-unrepresentable", target, io, reused)) + 1;
                        continue;
                    }

                    JsonValueSchema imported;

                    try
                    {
                        imported = Z.FromJsonSchema(schemaEntry.GetProperty("schema").GetRawText());
                    }
                    catch (JsonSchemaImportException exception)
                    {
                        _other.Add((new Difference("interop-in", zodCase.Name, null, "refused", "schema", exception.Code, target, io, reused, exception.Message), false));
                        continue;
                    }

                    for (int index = 0; index < ZodFixture.Documents.Length; index++)
                    {
                        ZodResult zod = zodCase.Results[index];
                        string text = ZodFixture.Documents[index].Text;

                        if (io == "output")
                        {
                            if (!zod.Accepted)
                            {
                                continue;
                            }

                            text = zod.OutputText ?? text;
                        }

                        Count(label);
                        bool accepted = imported.SafeParse(text).IsSuccess;
                        bool expected = io == "input" ? zod.Accepted : true;

                        if (accepted != expected)
                        {
                            bool inheritable = string.Equals(text, ZodFixture.Documents[index].Text, StringComparison.Ordinal)
                                && verdictDifferences.Contains((zodCase.Name, index))
                                && Outcomes[zodCase.Name][index].Accepted == accepted;

                            _other.Add((
                                new Difference(
                                    "interop-in",
                                    zodCase.Name,
                                    ZodFixture.Documents[index],
                                    "verdict",
                                    ZodComparer.State(expected),
                                    ZodComparer.State(accepted),
                                    target,
                                    io,
                                    reused,
                                    io == "output" ? $"zod output {text}" : string.Empty),
                                inheritable));
                        }
                    }
                }
            }
        }
    }

    private void CompareOpenApi(ZodCaseData zodCase, HashSet<(string, int)> verdictDifferences)
    {
        foreach (string io in new[] { "input", "output" })
        {
            JsonElement entry = zodCase.Schemas.GetProperty("openapi-3.0").GetProperty(io).GetProperty("inline");

            if (entry.TryGetProperty("error", out _))
            {
                continue;
            }

            Count("openapi-3.0 refusal");
            JsonValueSchema? imported = null;
            string code;

            try
            {
                imported = Z.FromJsonSchema(entry.GetProperty("schema").GetRawText());
                code = "imported";
            }
            catch (JsonSchemaImportException exception)
            {
                code = exception.Code;
            }

            if (!string.Equals(code, JsonSchemaImportCodes.UnsupportedDialect, StringComparison.Ordinal))
            {
                _other.Add((new Difference("openapi-refusal", zodCase.Name, null, "refused", "openapi-3.0 document", code, "openapi-3.0", io, "inline"), false));
            }

            if (imported is null || io != "input")
            {
                continue;
            }

            for (int index = 0; index < ZodFixture.Documents.Length; index++)
            {
                Count("openapi-3.0 misread");
                bool zod = zodCase.Results[index].Accepted;
                bool accepted = imported.SafeParse(ZodFixture.Documents[index].Text).IsSuccess;

                if (accepted != zod)
                {
                    _other.Add((
                        new Difference("openapi-misread", zodCase.Name, ZodFixture.Documents[index], "verdict", ZodComparer.State(zod), ZodComparer.State(accepted), "openapi-3.0", io, "inline"),
                        verdictDifferences.Contains((zodCase.Name, index)) && Outcomes[zodCase.Name][index].Accepted == accepted));
                }
            }
        }
    }

    private void CompareZodReader(HashSet<(string, int)> verdictDifferences)
    {
        foreach (JsonElement item in ZodFixture.ZodReads.GetProperty("cases").EnumerateArray())
        {
            string name = item.GetProperty("name").GetString()!;
            ZodCaseData? zodCase = ZodFixture.Cases.FirstOrDefault(c => c.Name == name);

            if (zodCase is null || !Outcomes.TryGetValue(name, out ImmutableArray<ZodOutcome> outcomes))
            {
                continue;
            }

            foreach (string target in EmitTargets)
            {
                JsonElement read = item.GetProperty("targets").GetProperty(target);

                if (read.TryGetProperty("refused", out JsonElement refused))
                {
                    _other.Add((new Difference("interop-out", name, null, "refused", "refused", "emitted", target, "input", "inline", refused.GetString()!), false));
                    continue;
                }

                string verdicts = read.GetProperty("results").GetString()!;

                for (int index = 0; index < verdicts.Length; index++)
                {
                    Count(Key("zod-reads", target));
                    bool zodReads = verdicts[index] == 'A';
                    bool zorg = outcomes[index].Accepted;

                    if (zodReads != zorg)
                    {
                        bool inheritable = verdictDifferences.Contains((name, index)) && zodCase.Results[index].Accepted == zodReads;

                        _other.Add((
                            new Difference(
                                "interop-out",
                                name,
                                ZodFixture.Documents[index],
                                "verdict",
                                ZodComparer.State(zodReads),
                                ZodComparer.State(zorg),
                                target,
                                "input",
                                "inline",
                                $"zod native {ZodComparer.State(zodCase.Results[index].Accepted)}"),
                            inheritable));
                    }
                }
            }
        }
    }

    /// <summary>What Zorg emits for each catalogue schema, as the Node tool reads it.</summary>
    public static string EmittedFixture()
    {
        List<string> lines = [];

        foreach ((string name, ZorgCase zorgCase) in ZodCatalogue.Cases.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            JsonObject targets = [];

            foreach (string target in EmitTargets)
            {
                JsonSchemaProfile profile = target == "draft-07" ? JsonSchemaProfile.Draft07 : JsonSchemaProfile.Draft202012;

                try
                {
                    targets[target] = new JsonObject { ["schema"] = zorgCase.Emit(new JsonSchemaOptions { Target = profile }).Schema };
                }
#pragma warning disable CA1031 // Any emit failure is recorded in the fixture.
                catch (Exception exception)
#pragma warning restore CA1031
                {
                    targets[target] = new JsonObject { ["error"] = exception.Message };
                }
            }

            lines.Add($"    {JsonSerializer.Serialize(name)}: {targets.ToJsonString()}");
        }

        return "{\n  \"cases\": {\n" + string.Join(",\n", lines) + "\n  }\n}\n";
    }

    public static string Percent(int part, int total) => total == 0 ? "n/a" : (100.0 * part / total).ToString("0.0", CultureInfo.InvariantCulture) + "%";
}
