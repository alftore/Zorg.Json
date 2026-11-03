using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Zorg.Json.ComplianceTests;

/// <summary>What the suite runner should do instead of comparing a verdict.</summary>
internal enum ExclusionKind
{
    /// <summary>Import must fail with the given code. Proves that nothing is dropped silently.</summary>
    ImportError,

    /// <summary>Not run, for a reason that holds for v1. It's an error if the case would pass.</summary>
    Skip,

    /// <summary>Not run until the ECMA-262 pattern translator lands. It's reported, not an error, if the case would pass.</summary>
    TemporaryRegex,
}

internal sealed record Exclusion(ExclusionKind Kind, string? Code, string Reason);

/// <summary>
/// The exclusions in <c>suite-manifest.json</c>. Keys are <c>draft/file</c>, then a group description,
/// or <c>group :: test</c> for one case. <c>*</c> stands for every group in a file.
/// </summary>
internal sealed class SuiteManifest
{
    private readonly Dictionary<string, Exclusion> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);

    private SuiteManifest()
    {
    }

    public static SuiteManifest Load()
    {
        SuiteManifest manifest = new();
        JsonElement root = JsonElement.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "suite-manifest.json")));

        foreach (JsonElement entry in root.GetProperty("exclusions").EnumerateArray())
        {
            ExclusionKind kind = Enum.Parse<ExclusionKind>(entry.GetProperty("kind").GetString()!);
            string? code = entry.TryGetProperty("code", out JsonElement value) ? value.GetString() : null;
            Exclusion exclusion = new(kind, code, entry.GetProperty("reason").GetString()!);

            foreach (JsonProperty file in entry.GetProperty("cases").EnumerateObject())
            {
                foreach (JsonElement group in file.Value.EnumerateArray())
                {
                    manifest._entries.Add($"{file.Name}|{group.GetString()}", exclusion);
                }
            }
        }

        return manifest;
    }

    public IEnumerable<Exclusion> All => _entries.Values.Distinct();

    public IEnumerable<string> Unused => _entries.Keys.Where(key => !_used.Contains(key));

    public Exclusion? ForGroup(string file, string group)
    {
        lock (_used)
        {
            foreach (string key in (string[])[$"{file}|*", $"{file}|{group}"])
            {
                if (_entries.TryGetValue(key, out Exclusion? exclusion))
                {
                    _used.Add(key);
                    return exclusion;
                }
            }
        }

        return null;
    }

    public Exclusion? ForTest(string file, string group, string test)
    {
        string key = $"{file}|{group} :: {test}";

        lock (_used)
        {
            if (_entries.TryGetValue(key, out Exclusion? exclusion))
            {
                _used.Add(key);
                return exclusion;
            }
        }

        return null;
    }
}
