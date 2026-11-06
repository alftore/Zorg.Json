using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Zorg.Json.ComplianceTests;

/// <summary>
/// Runs <c>jv</c>, the command line of santhosh-tekuri/jsonschema. It shares no code or runtime with Zorg or JsonSchema.Net, keeps
/// numbers exact, counts code points and has a regex engine of its own, so it can judge what JsonSchema.Net can't.
/// Set <c>ZORG_JV</c> to the path of the executable. The tests that need it are skipped without it.
/// </summary>
internal sealed class GoValidator : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("zorg-jv-").FullName;

    public static string? Executable => Environment.GetEnvironmentVariable("ZORG_JV") is { Length: > 0 } path ? path : null;

    /// <summary>
    /// The verdict on each instance, or null when <c>jv</c> refuses the schema, and then <paramref name="refusal"/> says why.
    /// A verdict is null for an instance that made <c>jv</c> crash.
    /// </summary>
    /// <param name="schema">The schema text.</param>
    /// <param name="instances">Instances, each valid JSON.</param>
    /// <param name="draft">The draft for a schema without <c>$schema</c>: 4, 6, 7 or 2020.</param>
    /// <param name="refusal">The output of <c>jv</c> when it refuses the schema.</param>
    /// <param name="assertFormat">Checks <c>format</c> from 2019-09 on. Before that <c>jv</c> always checks it.</param>
    public bool?[]? Judge(string schema, IReadOnlyList<string> instances, string draft, out string? refusal, bool assertFormat = false)
    {
        UTF8Encoding encoding = new(encoderShouldEmitUTF8Identifier: false);
        File.WriteAllText(Path.Combine(_folder, "s.json"), schema, encoding);

        for (int index = 0; index < instances.Count; index++)
        {
            File.WriteAllText(Path.Combine(_folder, $"i{index}.json"), instances[index], encoding);
        }

        bool?[] verdicts = new bool?[instances.Count];
        int start = 0;
        refusal = null;

        while (true)
        {
            string output = Run(draft, start, instances.Count, assertFormat);

            if (!output.Contains("schema s.json: ok", StringComparison.Ordinal))
            {
                refusal = output;
                return null;
            }

            int next = start;

            foreach (string line in output.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith("instance i", StringComparison.Ordinal)))
            {
                if (line.EndsWith(": ok", StringComparison.Ordinal) || line.EndsWith(": failed", StringComparison.Ordinal))
                {
                    verdicts[next++] = line.EndsWith(": ok", StringComparison.Ordinal);
                }
            }

            // jv prints the instances in order and a crash ends the run, so the first one without a verdict caused it.
            if (next >= instances.Count)
            {
                return verdicts;
            }

            start = next + 1;

            if (start >= instances.Count)
            {
                return verdicts;
            }
        }
    }

    private string Run(string draft, int from, int to, bool assertFormat)
    {
        ProcessStartInfo start = new(Executable!)
        {
            WorkingDirectory = _folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in new[] { "-o", "flag", "-d", draft, "s.json" })
        {
            start.ArgumentList.Add(argument);
        }

        if (assertFormat)
        {
            start.ArgumentList.Add("-f");
        }

        for (int index = from; index < to; index++)
        {
            start.ArgumentList.Add($"i{index}.json");
        }

        using Process process = Process.Start(start)!;
        System.Threading.Tasks.Task<string> errors = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output + errors.GetAwaiter().GetResult();
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
