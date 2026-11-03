using System;
using System.Text.RegularExpressions;

namespace Zorg.Json.Validation;

internal static class PatternCompiler
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Prefers the linear-time engine, which rejects lookarounds and backreferences. Those fall back to
    /// the backtracking engine with a timeout.
    /// </summary>
    public static Regex Compile(string pattern)
    {
        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        }
        catch (NotSupportedException)
        {
            return new Regex(pattern, RegexOptions.CultureInvariant, Timeout);
        }
    }
}
