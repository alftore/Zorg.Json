using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Zorg.Json.ComplianceTests;

/// <summary>Compares a Zod result with a Zorg result: verdict, output, then issue code, then issue path.</summary>
internal static class ZodComparer
{
    public static string State(bool accepted) => accepted ? "accept" : "reject";

    /// <returns>The kind of disagreement and a short description, or null when they agree.</returns>
    public static (string Kind, string Detail)? Compare(ZodResult zod, ZodOutcome zorg, string documentText)
    {
        if (zorg.Crash is not null)
        {
            return ("crash", zorg.Crash);
        }

        if (zod.Accepted != zorg.Accepted)
        {
            return ("verdict", zod.Accepted ? $"zod output {zod.OutputText ?? "unchanged"}" : string.Join(' ', zod.Issues) + " / zorg " + string.Join(' ', zorg.Issues));
        }

        if (zod.Accepted)
        {
            using JsonDocument expected = JsonDocument.Parse(zod.OutputText ?? documentText);
            using JsonDocument actual = JsonDocument.Parse(zorg.Output!);
            return JsonEquals(expected.RootElement, actual.RootElement) ? null : ("output", $"zod {Trim(expected.RootElement.GetRawText())} / zorg {Trim(actual.RootElement.GetRawText())}");
        }

        ImmutableSortedSet<string> zodCodes = Codes(zod.Issues);
        ImmutableSortedSet<string> zorgCodes = Codes(zorg.Issues);

        if (!zodCodes.SetEquals(zorgCodes))
        {
            return ("code", $"zod {string.Join(' ', zod.Issues)} / zorg {string.Join(' ', zorg.Issues)}");
        }

        return zod.Issues.ToImmutableSortedSet().SetEquals(zorg.Issues)
            ? null
            : ("path", $"zod {string.Join(' ', zod.Issues)} / zorg {string.Join(' ', zorg.Issues)}");
    }

    public static bool JsonEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        switch (left.ValueKind)
        {
            case JsonValueKind.String:
                return StringEquals(left, right);
            case JsonValueKind.Number:
                return left.TryGetDouble(out double a) && right.TryGetDouble(out double b)
                    ? a.Equals(b) || (a == 0 && b == 0)
                    : string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);
            case JsonValueKind.Array:
                return left.GetArrayLength() == right.GetArrayLength()
                    && left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => JsonEquals(pair.First, pair.Second));
            case JsonValueKind.Object:
                Dictionary<string, JsonElement> l = Properties(left);
                Dictionary<string, JsonElement> r = Properties(right);
                return l.Count == r.Count && l.All(entry => r.TryGetValue(entry.Key, out JsonElement other) && JsonEquals(entry.Value, other));
            default:
                return true;
        }
    }

    private static bool StringEquals(JsonElement left, JsonElement right)
    {
        try
        {
            return string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal);
        }
        catch (InvalidOperationException)
        {
            // A lone surrogate can't be read as a string, so compare the escaped text.
            return string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<string, JsonElement> Properties(JsonElement element)
    {
        Dictionary<string, JsonElement> result = new(StringComparer.Ordinal);

        foreach (JsonProperty property in element.EnumerateObject())
        {
            result[property.Name] = property.Value;
        }

        return result;
    }

    private static ImmutableSortedSet<string> Codes(ImmutableArray<string> issues) =>
        [.. issues.Select(issue => issue[..issue.IndexOf('@', StringComparison.Ordinal)])];

    private static string Trim(string text) => text.Length <= 60 ? text : string.Create(CultureInfo.InvariantCulture, $"{text[..57]}...");
}
