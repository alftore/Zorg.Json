using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zorg.Json.Llm;

/// <summary>What to do with a keyword in an emitted schema.</summary>
internal enum KeywordVerdict
{
    /// <summary>The provider accepts it.</summary>
    Allowed,

    /// <summary>Move it to <c>description</c>.</summary>
    Relocate,

    /// <summary>Remove it with a diagnostic, because it means nothing to a model.</summary>
    Remove,

    /// <summary>Remove it with no diagnostic, because the provider's behaviour already implies it.</summary>
    Implied,
}

/// <summary>Decides, from a capability table, whether one keyword may stay.</summary>
internal static class CapabilityRules
{
    public static KeywordVerdict Check(SchemaCapabilities capabilities, string keyword, JsonNode? value)
    {
        if (keyword == "$comment")
        {
            return KeywordVerdict.Remove;
        }

        if (keyword == "additionalProperties" && !capabilities.Keywords.Contains(keyword) && value?.GetValueKind() == JsonValueKind.False)
        {
            return KeywordVerdict.Implied;
        }

        if (!capabilities.Keywords.Contains(keyword))
        {
            return KeywordVerdict.Relocate;
        }

        return keyword switch
        {
            "format" => CheckFormat(capabilities, value),
            "minItems" when capabilities.MaxMinItems is { } limit && value?.GetValue<int>() > limit => KeywordVerdict.Relocate,
            "pattern" when value is not null && (RegexScanner.Scan(value.GetValue<string>()).Features & capabilities.UnsupportedRegex) != RegexFeatures.None => KeywordVerdict.Relocate,
            "enum" when capabilities.ScalarEnumsOnly && value is JsonArray values && values.Any(IsComplex) => KeywordVerdict.Relocate,
            _ => KeywordVerdict.Allowed,
        };
    }

    private static KeywordVerdict CheckFormat(SchemaCapabilities capabilities, JsonNode? value)
    {
        string? format = value?.GetValue<string>();

        if (format is null || capabilities.Formats is null || capabilities.Formats.Contains(format))
        {
            return KeywordVerdict.Allowed;
        }

        // Zorg writes these for CLR number types. They only name a storage size.
        return format is "int32" or "int64" or "float" or "double" ? KeywordVerdict.Remove : KeywordVerdict.Relocate;
    }

    private static bool IsComplex(JsonNode? value) => value is JsonObject or JsonArray;
}

/// <summary>Finds the regular expression features a provider may refuse, without compiling the pattern.</summary>
internal static class RegexScanner
{
    public static (RegexFeatures Features, int LargestBound) Scan(string pattern)
    {
        RegexFeatures features = RegexFeatures.None;
        int largestBound = 0;
        bool inClass = false;

        for (int index = 0; index < pattern.Length; index++)
        {
            char current = pattern[index];
            char next = index + 1 < pattern.Length ? pattern[index + 1] : '\0';

            if (current == '\\')
            {
                if (!inClass)
                {
                    features |= Escape(next);
                }

                index++;
            }
            else if (inClass)
            {
                inClass = current != ']';
            }
            else if (current == '[')
            {
                inClass = true;
            }
            else if (current == '(' && IsLookaround(pattern.AsSpan(index)))
            {
                features |= RegexFeatures.Lookaround;
            }
            else if (current == '{')
            {
                largestBound = Math.Max(largestBound, Bound(pattern.AsSpan(index + 1)));
            }
        }

        return (features, largestBound);
    }

    private static RegexFeatures Escape(char escaped) => escaped switch
    {
        'b' or 'B' => RegexFeatures.WordBoundary,
        'k' or (>= '1' and <= '9') => RegexFeatures.Backreference,
        _ => RegexFeatures.None,
    };

    private static bool IsLookaround(ReadOnlySpan<char> text) =>
        text.StartsWith("(?=") || text.StartsWith("(?!") || text.StartsWith("(?<=") || text.StartsWith("(?<!");

    private static int Bound(ReadOnlySpan<char> text)
    {
        int largest = 0;
        int current = 0;

        foreach (char character in text)
        {
            if (character is >= '0' and <= '9')
            {
                current = Math.Min((current * 10) + (character - '0'), 1_000_000);
            }
            else if (character == ',')
            {
                largest = current;
                current = 0;
            }
            else
            {
                return character == '}' ? Math.Max(largest, current) : 0;
            }
        }

        return 0;
    }
}
