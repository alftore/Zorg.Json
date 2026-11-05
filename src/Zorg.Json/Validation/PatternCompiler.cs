using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading;

namespace Zorg.Json.Validation;

internal static class PatternCompiler
{
    // NonBacktracking stops matching \n once a pattern separates about 125 sets of characters (seen on .NET 10.0.14).
    private const long MaxLinearMinterms = 100;

    // Compiling costs milliseconds for patterns with \p{...}, and a document is checked at import and again when its plan
    // is built. Longer patterns are rare and aren't kept, and a full cache is emptied rather than grown.
    private const int MaxCachedLength = 4_096;
    private const int MaxCachedCount = 1_024;
    private const long MaxCachedLengthInTotal = 262_144;

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    private static long _cachedLength;

    /// <summary>How many patterns are kept.</summary>
    public static int CachedCount => Cache.Count;

    /// <summary>
    /// Compiles an ECMA-262 pattern. Prefers the linear-time engine, which rejects lookarounds and backreferences.
    /// Those fall back to the backtracking engine with a timeout, and so does a pattern that distinguishes too many
    /// characters, which most <c>\p{...}</c> escapes do because their astral part is spelled out. <c>\b</c> and
    /// <c>\B</c> translate to lookarounds. The same text gives the same <see cref="Regex"/>, which is safe to share.
    /// </summary>
    /// <exception cref="ArgumentException">The pattern isn't valid in ECMA-262 Unicode mode, or can't be translated.</exception>
    public static Regex Compile(string pattern)
    {
        if (Cache.TryGetValue(pattern, out Regex? known))
        {
            return known;
        }

        Regex regex = Create(pattern);

        if (pattern.Length <= MaxCachedLength)
        {
            Remember(pattern, regex);
        }

        return regex;
    }

    private static void Remember(string pattern, Regex regex)
    {
        if (Cache.Count >= MaxCachedCount || Interlocked.Read(ref _cachedLength) + pattern.Length > MaxCachedLengthInTotal)
        {
            Cache.Clear();
            Interlocked.Exchange(ref _cachedLength, 0);
        }

        if (Cache.TryAdd(pattern, regex))
        {
            Interlocked.Add(ref _cachedLength, pattern.Length);
        }
    }

    private static Regex Create(string pattern)
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
