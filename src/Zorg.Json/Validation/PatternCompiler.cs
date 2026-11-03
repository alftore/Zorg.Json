using System;
using System.Text.RegularExpressions;

namespace Zorg.Json.Validation;

internal static class PatternCompiler
{
    // NonBacktracking stops matching \n once a pattern separates about 125 sets of characters (seen on .NET 10.0.14).
    private const long MaxLinearMinterms = 100;

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Compiles an ECMA-262 pattern. Prefers the linear-time engine, which rejects lookarounds and backreferences.
    /// Those fall back to the backtracking engine with a timeout, and so does a pattern that distinguishes too many
    /// characters, which most <c>\p{...}</c> escapes do because their astral part is spelled out. <c>\b</c> and
    /// <c>\B</c> translate to lookarounds.
    /// </summary>
    /// <exception cref="ArgumentException">The pattern isn't valid in ECMA-262 Unicode mode, or can't be translated.</exception>
    public static Regex Compile(string pattern)
    {
        string translated = EcmaRegexTranslator.Translate(pattern, out long maxMinterms);

        if (maxMinterms <= MaxLinearMinterms)
        {
            try
            {
                return new Regex(translated, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
            }
            catch (NotSupportedException)
            {
                // Falls through to the backtracking engine.
            }
        }

        return new Regex(translated, RegexOptions.CultureInvariant, Timeout);
    }
}
