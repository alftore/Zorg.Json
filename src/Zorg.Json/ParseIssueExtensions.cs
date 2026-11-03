using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace Zorg.Json;

/// <summary>Formatters for the issues of a failed parse.</summary>
public static class ParseIssueExtensions
{
    /// <summary>
    /// Groups messages by JSON Pointer, the shape ASP.NET Core's <c>ValidationProblemDetails</c> takes.
    /// Zod equivalent: <c>z.flattenError</c>.
    /// </summary>
    /// <param name="issues">The issues to format.</param>
    /// <returns>Messages keyed by <see cref="ParseIssue.InstanceLocation"/>.</returns>
    /// <example>
    /// <code>
    /// ParseResult&lt;string&gt; result = Z.String().MinLength(3).SafeParse("\"a\"");
    /// Dictionary&lt;string, string[]&gt; errors = result.Issues.ToDictionary();
    /// </code>
    /// </example>
    public static Dictionary<string, string[]> ToDictionary(this ImmutableArray<ParseIssue> issues) =>
        issues
            .GroupBy(issue => issue.InstanceLocation)
            .ToDictionary(group => group.Key, group => group.Select(issue => issue.Message).ToArray());

    /// <summary>Formats the issues for logs and consoles. Zod equivalent: <c>z.prettifyError</c>.</summary>
    /// <param name="issues">The issues to format.</param>
    /// <returns>Two lines per issue: the message, and where it occurred.</returns>
    /// <example>
    /// <code>
    /// ParseResult&lt;string&gt; result = Z.String().MinLength(3).SafeParse("\"a\"");
    /// Console.WriteLine(result.Issues.ToPrettyString());
    /// </code>
    /// </example>
    public static string ToPrettyString(this ImmutableArray<ParseIssue> issues)
    {
        StringBuilder text = new();

        foreach (ParseIssue issue in issues)
        {
            text.Append("✖ ").Append(issue.Message).Append('\n');
            text.Append("  → at ").Append(issue.InstanceLocation.Length == 0 ? "/" : issue.InstanceLocation).Append('\n');
        }

        return text.ToString();
    }
}
