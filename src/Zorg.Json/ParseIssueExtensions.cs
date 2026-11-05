using System;
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

    /// <summary>
    /// Nests the issues like the value they describe, so a form can ask for the errors of one field.
    /// Zod equivalent: <c>z.treeifyError</c>.
    /// </summary>
    /// <param name="issues">The issues to format.</param>
    /// <returns>The root of the tree. Each issue lands at its <see cref="ParseIssue.InstanceLocation"/>, with the JSON Pointer escapes undone.</returns>
    /// <example>
    /// <code>
    /// ParseResult&lt;JsonElement&gt; result = Z.Object().Property("name", Z.String().MinLength(3)).SafeParse("{\"name\":\"a\"}");
    /// IssueTree tree = result.Issues.ToTree();
    /// string message = tree.Children["name"].Errors[0];
    /// </code>
    /// </example>
    public static IssueTree ToTree(this ImmutableArray<ParseIssue> issues)
    {
        IssueTree root = new();
        Dictionary<IssueTree, List<string>> messages = new(ReferenceEqualityComparer.Instance);

        foreach (ParseIssue issue in issues.IsDefault ? [] : issues)
        {
            IssueTree node = root;

            if (issue.InstanceLocation.Length > 0)
            {
                foreach (string token in issue.InstanceLocation.Split('/').Skip(1))
                {
                    string key = token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

                    if (!node.Nodes.TryGetValue(key, out IssueTree? child))
                    {
                        child = new IssueTree();
                        node.Nodes.Add(key, child);
                    }

                    node = child;
                }
            }

            if (!messages.TryGetValue(node, out List<string>? list))
            {
                messages.Add(node, list = []);
            }

            list.Add(issue.Message);
        }

        foreach ((IssueTree node, List<string> list) in messages)
        {
            node.Errors = [.. list];
        }

        return root;
    }

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
