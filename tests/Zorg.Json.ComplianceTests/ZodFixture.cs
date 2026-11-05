using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zorg.Json.ComplianceTests;

internal sealed record ZodDocument(int Index, string Text, ImmutableHashSet<string> Tags);

/// <summary>What Zod did with one document. A null <see cref="OutputText"/> on success means "unchanged".</summary>
internal sealed record ZodResult(bool Accepted, string? OutputText, ImmutableArray<string> Issues, string? Crash);

internal sealed record ZodCaseData(string Name, string Group, ImmutableArray<ZodResult> Results, JsonElement Schemas);

/// <summary>One entry of <c>known-differences.json</c>. Every filter that is set must match.</summary>
internal sealed record KnownDifference(
    string Id,
    string Category,
    string Status,
    string Reason,
    string Link,
    string Repro,
    string Part,
    ImmutableArray<string> Cases,
    ImmutableArray<string> DocTags,
    ImmutableArray<string> Docs,
    ImmutableArray<string> Kinds,
    string? Zod,
    string? Zorg,
    ImmutableArray<string> Targets,
    ImmutableArray<string> Ios,
    string? DetailPattern)
{
    public bool Matches(Difference difference)
    {
        if (!Part.Split('|').Contains(difference.Part) || !Kinds.Contains(difference.Kind))
        {
            return false;
        }

        if (!Cases.Any(pattern => Glob(pattern, difference.Case)))
        {
            return false;
        }

        if (!Targets.IsDefaultOrEmpty && !Targets.Contains(difference.Target))
        {
            return false;
        }

        if (!Ios.IsDefaultOrEmpty && !Ios.Contains(difference.Io))
        {
            return false;
        }

        if (Zod is not null && !string.Equals(Zod, difference.Zod, StringComparison.Ordinal))
        {
            return false;
        }

        if (Zorg is not null && !string.Equals(Zorg, difference.Zorg, StringComparison.Ordinal))
        {
            return false;
        }

        if (DetailPattern is not null && !Regex.IsMatch(difference.Detail, DetailPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            return false;
        }

        if (difference.Document is null)
        {
            return DocTags.IsDefaultOrEmpty && Docs.IsDefaultOrEmpty;
        }

        if (!DocTags.IsDefaultOrEmpty && !DocTags.Any(difference.Document.Tags.Contains))
        {
            return false;
        }

        return Docs.IsDefaultOrEmpty || Docs.Contains(difference.Document.Text);
    }

    private static bool Glob(string pattern, string value) =>
        Regex.IsMatch(value, "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$", RegexOptions.CultureInvariant);
}

/// <summary>A disagreement between Zod and Zorg that has to be listed in known-differences.json.</summary>
internal sealed record Difference(
    string Part,
    string Case,
    ZodDocument? Document,
    string Kind,
    string Zod,
    string Zorg,
    string Target = "",
    string Io = "",
    string Reused = "",
    string Detail = "")
{
    public override string ToString() =>
        $"{Part} {Case} {Target} {Io} {Reused} doc#{Document?.Index} {Kind}: zod={Zod} zorg={Zorg} {Detail}".Replace("  ", " ", StringComparison.Ordinal);
}

/// <summary>Reads the committed fixtures once.</summary>
internal static class ZodFixture
{
    public static string FixturesSourcePath { get; } = Metadata("ZodFixturesPath");

    public static string ToolPath { get; } = Metadata("ZodToolPath");

    public static string Version { get; }

    public static ImmutableArray<ZodDocument> Documents { get; }

    public static ImmutableArray<ZodCaseData> Cases { get; }

    public static ImmutableArray<KnownDifference> KnownDifferences { get; }

    public static JsonElement ZodReads { get; }

    static ZodFixture()
    {
        using JsonDocument fixture = JsonDocument.Parse(File.ReadAllText(OutputPath("zod-comparison.json")));
        JsonElement root = fixture.RootElement;
        Version = root.GetProperty("zod").GetString()!;

        Documents =
        [
            .. root.GetProperty("documents").EnumerateArray().Select((document, index) => new ZodDocument(
                index,
                document.GetProperty("text").GetString()!,
                [.. document.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()!)])),
        ];

        Cases =
        [
            .. root.GetProperty("cases").EnumerateArray().Select(item => new ZodCaseData(
                item.GetProperty("name").GetString()!,
                item.GetProperty("group").GetString()!,
                [.. item.GetProperty("results").EnumerateArray().Select(ReadResult)],
                item.GetProperty("schemas").Clone())),
        ];

        using JsonDocument known = JsonDocument.Parse(File.ReadAllText(OutputPath("known-differences.json")));
        KnownDifferences = [.. known.RootElement.GetProperty("differences").EnumerateArray().Select(ReadKnown)];

        using JsonDocument reads = JsonDocument.Parse(File.ReadAllText(OutputPath("zod-reads-zorg.json")));
        ZodReads = reads.RootElement.Clone();
    }

    public static string OutputPath(string file) => Path.Combine(AppContext.BaseDirectory, "Fixtures", file);

    private static string Metadata(string key) => typeof(ZodFixture).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == key)
        .Value!;

    private static ZodResult ReadResult(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return new ZodResult(true, null, [], null);
            case JsonValueKind.Array:
                return new ZodResult(false, null, [.. element.EnumerateArray().Select(issue => issue.GetString()!)], null);
            default:
                return element.TryGetProperty("o", out JsonElement output)
                    ? new ZodResult(true, output.GetString(), [], null)
                    : new ZodResult(false, null, [], element.GetProperty("x").GetString());
        }
    }

    private static KnownDifference ReadKnown(JsonElement element)
    {
        ImmutableArray<string> List(string name) => element.TryGetProperty(name, out JsonElement list)
            ? [.. list.EnumerateArray().Select(item => item.GetString()!)]
            : [];

        string Text(string name) => element.TryGetProperty(name, out JsonElement text) ? text.GetString()! : string.Empty;

        string? Optional(string name) => element.TryGetProperty(name, out JsonElement text) ? text.GetString() : null;

        return new KnownDifference(
            Text("id"),
            Text("category"),
            Text("status"),
            Text("reason"),
            Text("link"),
            Text("repro"),
            element.TryGetProperty("part", out JsonElement part) ? part.GetString()! : "behaviour",
            List("cases"),
            List("docTags"),
            List("docs"),
            List("kinds"),
            Optional("zod"),
            Optional("zorg"),
            List("targets"),
            List("io"),
            Optional("detail"));
    }
}
