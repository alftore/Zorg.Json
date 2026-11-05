using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Zorg.Json.Model;
using Zorg.Json.Validation;

namespace Zorg.Json.Sampling;

/// <summary>Strings that fit a schema: from its pattern, a sample per format, then plain text cut to length.</summary>
internal static class SampleStrings
{
    /// <summary>The plain sample. <see cref="SampleGenerator"/> looks for it when a typed property turns out to need a date.</summary>
    public const string Plain = "string";

    /// <summary>Longer than any string worth generating. A greater <c>minLength</c> counts as unsatisfiable here.</summary>
    public const int MaxLength = 1 << 20;

    public static IEnumerable<string> Candidates(SchemaNode view, int breadth)
    {
        int minimum = view.MinLength ?? 0;
        int maximum = view.MaxLength ?? int.MaxValue;

        if (minimum > MaxLength)
        {
            yield break;
        }

        string[] patterns = [.. Patterns(view)];

        foreach (string pattern in patterns)
        {
            foreach (string text in PatternSampler.Generate(pattern, minimum, maximum, breadth))
            {
                yield return text;
            }
        }

        if (patterns.Length > 1)
        {
            // Unanchored patterns can each match their own part of one string.
            yield return string.Concat(patterns.Select(pattern => PatternSampler.Generate(pattern, 0, int.MaxValue, 1).FirstOrDefault()));
        }

        if (view.ContentEncoding == "base64")
        {
            yield return "c3RyaW5n";
        }

        if (view.Format is { } format && StringFormats.IsKnown(format))
        {
            for (int index = 0; index < breadth; index++)
            {
                yield return Stretch(format, Format(format, index), minimum);
            }

            if (Shortest(format) is { } shortest)
            {
                yield return shortest;
            }
        }

        yield return Fit(Plain, minimum, maximum);

        for (int index = 1; index < breadth; index++)
        {
            yield return Fit(((char)('a' + ((index - 1) % 26))).ToString(), minimum, maximum);
            yield return Fit(string.Create(CultureInfo.InvariantCulture, $"{Plain}{index}"), minimum, maximum);
        }

        yield return Fit(string.Empty, minimum, maximum);
    }

    /// <summary>A valid value of the format. Each index gives a different one, for <c>uniqueItems</c>.</summary>
    internal static string Format(string format, int index)
    {
        string number = index == 0 ? string.Empty : index.ToString(CultureInfo.InvariantCulture);
        string day = string.Create(CultureInfo.InvariantCulture, $"2024-01-{(index % 28) + 1:00}");
        string time = string.Create(CultureInfo.InvariantCulture, $"12:{index / 60 % 60:00}:{index % 60:00}Z");

        return format switch
        {
            "date" => day,
            "date-time" => $"{day}T{time}",
            "time" => time,
            "duration" => string.Create(CultureInfo.InvariantCulture, $"P{index + 1}D"),
            "email" or "idn-email" => $"user{number}@example.com",
            "hostname" or "idn-hostname" => $"example{number}.com",
            "ipv4" => string.Create(CultureInfo.InvariantCulture, $"192.0.2.{(index % 254) + 1}"),
            "ipv6" => string.Create(CultureInfo.InvariantCulture, $"2001:db8::{index + 1:x}"),
            "uri" or "iri" or "uri-reference" or "iri-reference" => $"https://example.com/{number}",
            "uri-template" => $"https://example.com/{number}{{id}}",
            "json-pointer" => $"/path{number}",
            "relative-json-pointer" => index.ToString(CultureInfo.InvariantCulture),
            "regex" => $"^a{number}$",
            "uuid" => string.Create(CultureInfo.InvariantCulture, $"123e4567-e89b-42d3-a456-{index:x12}"),
            _ => Plain,
        };
    }

    private static IEnumerable<string> Patterns(SchemaNode view)
    {
        if (view.Pattern is { } pattern)
        {
            yield return pattern;
        }

        foreach (SchemaNode residue in view.AllOf)
        {
            if (residue.Pattern is { } extra)
            {
                yield return extra;
            }
        }
    }

    private static string? Shortest(string format) => format switch
    {
        "email" or "idn-email" => "a@b",
        "hostname" or "idn-hostname" or "uri-reference" or "iri-reference" or "regex" or "uri-template" => "a",
        "ipv6" => "::",
        "ipv4" => "0.0.0.0",
        "uri" or "iri" => "a:b",
        "json-pointer" => string.Empty,
        _ => null,
    };

    /// <summary>Pads where the format has room, so that a sample can meet <c>minLength</c>.</summary>
    private static string Stretch(string format, string text, int minimum)
    {
        int missing = minimum - JsonStrings.CodePointCount(text);

        if (missing <= 0)
        {
            return text;
        }

        string padding = new('a', missing);

        return format switch
        {
            "email" or "idn-email" or "hostname" or "idn-hostname" => padding + text,
            "uri" or "iri" or "uri-reference" or "iri-reference" or "json-pointer" or "uri-template" => text + padding,
            "regex" => $"^{padding}{text[1..]}",
            _ => text,
        };
    }

    private static string Fit(string text, int minimum, int maximum)
    {
        string source = text.Length == 0 && minimum > 0 ? Plain : text;
        StringBuilder fitted = new();

        for (int length = 0; length < maximum && (length < minimum || length < source.Length); length++)
        {
            fitted.Append(source[length % source.Length]);
        }

        return fitted.ToString();
    }
}
