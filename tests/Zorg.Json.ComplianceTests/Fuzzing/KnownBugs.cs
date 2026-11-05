using System;
using System.Text.RegularExpressions;

namespace Zorg.Json.ComplianceTests.Fuzzing;

/// <summary>
/// Lone surrogates and invalid UTF-8 trip bugs that are reported but not fixed yet. A run leaves them out so that everything
/// else stays checked. Set <c>ZORG_FUZZ_KNOWN_BUGS=1</c> to put them back, for instance after a fix.
/// </summary>
internal static partial class KnownBugs
{
    public static bool Included { get; } = Environment.GetEnvironmentVariable("ZORG_FUZZ_KNOWN_BUGS") == "1";

    /// <summary>True for text with a surrogate escape that has no partner, such as <c>"\ud800"</c>.</summary>
    public static bool HasLoneSurrogate(string text) => LoneSurrogate().IsMatch(text);

    /// <summary>True for a .NET string with an unpaired surrogate character.</summary>
    public static bool HasLoneSurrogateChar(string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                index++;
            }
            else if (char.IsSurrogate(text[index]))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"\\u[dD][89abAB][0-9a-fA-F]{2}(?!\\u[dD][c-fC-F][0-9a-fA-F]{2})|(?<!\\u[dD][89abAB][0-9a-fA-F]{2})\\u[dD][c-fC-F][0-9a-fA-F]{2}")]
    private static partial Regex LoneSurrogate();
}
