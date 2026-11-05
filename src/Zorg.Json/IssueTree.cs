using System.Collections.Generic;
using System.Collections.Immutable;

namespace Zorg.Json;

/// <summary>
/// The issues of a failed parse, nested like the value they describe. Create one with
/// <see cref="ParseIssueExtensions.ToTree"/>. Zod equivalent: the result of <c>z.treeifyError</c>.
/// </summary>
public sealed class IssueTree
{
    internal IssueTree()
    {
    }

    /// <summary>The messages of the issues at this value itself. Zod equivalent: <c>errors</c>.</summary>
    /// <example>
    /// <code>
    /// IssueTree tree = Z.String().MinLength(3).SafeParse("\"a\"").Issues.ToTree();
    /// string first = tree.Errors[0];
    /// </code>
    /// </example>
    public ImmutableArray<string> Errors { get; internal set; } = [];

    /// <summary>
    /// The trees of the values below this one, keyed by property name or array index, in the order they were first reported.
    /// Zod equivalent: <c>properties</c> and <c>items</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// IssueTree tree = Z.Array(Z.String().MinLength(3)).SafeParse("[\"abc\",\"a\"]").Issues.ToTree();
    /// IssueTree second = tree.Children["1"];
    /// </code>
    /// </example>
    public IReadOnlyDictionary<string, IssueTree> Children => Nodes;

    internal Dictionary<string, IssueTree> Nodes { get; } = [];
}
