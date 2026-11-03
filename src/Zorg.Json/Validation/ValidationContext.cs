using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Json.Model;

namespace Zorg.Json.Validation;

internal sealed class ValidationContext
{
    private const int MaxDepth = 512;

    private readonly List<PathSegment> _path;
    private List<ParseIssue>? _issues;
    private List<PendingRefinement>? _pending;

    public ValidationContext()
        : this([])
    {
    }

    private ValidationContext(List<PathSegment> path) => _path = path;

    public int IssueCount => _issues?.Count ?? 0;

    public bool HasIssues => IssueCount > 0;

    public bool HasPendingRefinements => _pending is not null;

    public int RewriteCount { get; private set; }

    public int Depth { get; private set; }

    public ImmutableArray<ParseIssue> ToIssues() => _issues is null ? [] : [.. _issues];

    public void Rewritten() => RewriteCount++;

    public bool Enter()
    {
        if (++Depth > MaxDepth)
        {
            Depth--;
            Report(IssueCodes.InvalidValue, "$ref", "invalid_value.depth", "maximum", MaxDepth);
            return false;
        }

        return true;
    }

    public void Leave() => Depth--;

    public void Push(string name) => _path.Add(new PathSegment(name, 0));

    public void Push(int index) => _path.Add(new PathSegment(null, index));

    public void Pop() => _path.RemoveAt(_path.Count - 1);

    /// <summary>Evaluates a branch whose issues only count if the caller says so. Shares the path.</summary>
    public ValidationContext Fork() => new(_path) { Depth = Depth };

    public void Adopt(ValidationContext fork)
    {
        RewriteCount += fork.RewriteCount;

        if (fork._pending is not null)
        {
            (_pending ??= []).AddRange(fork._pending);
        }
    }

    public void AdoptIssues(ValidationContext fork)
    {
        if (fork._issues is not null)
        {
            (_issues ??= []).AddRange(fork._issues);
        }
    }

    public void Report(string code, string keyword, string messageKey, string parameter, object? value, params object?[] messageArguments) =>
        Add(new ParseIssue
        {
            Code = code,
            Message = Messages.Get(messageKey, messageArguments),
            InstanceLocation = Pointer(),
            Keyword = keyword,
            Parameters = ImmutableDictionary<string, object?>.Empty.Add(parameter, value),
        });

    public void ReportRefinement(SchemaRefinement refinement) =>
        ReportRefinement(refinement, Pointer());

    public void ReportInvalidJson(JsonException exception) =>
        Add(new ParseIssue
        {
            Code = IssueCodes.InvalidJson,
            Message = Messages.Get(IssueCodes.InvalidJson),
            InstanceLocation = string.Empty,
            Parameters = Detail(exception),
        });

    public void ReportDeserializationFailed(JsonException exception) =>
        Add(new ParseIssue
        {
            Code = IssueCodes.DeserializationFailed,
            Message = Messages.Get(IssueCodes.DeserializationFailed),
            InstanceLocation = Pointer(),
            Parameters = Detail(exception),
        });

    public void Defer(SchemaRefinement refinement, JsonElement value) =>
        (_pending ??= []).Add(new PendingRefinement(refinement, value.Clone(), Pointer()));

    public async ValueTask RunPendingAsync(CancellationToken cancellationToken)
    {
        if (_pending is null)
        {
            return;
        }

        foreach (PendingRefinement pending in _pending)
        {
            if (!await pending.Refinement.CheckAsync(pending.Value, cancellationToken).ConfigureAwait(false))
            {
                ReportRefinement(pending.Refinement, pending.Location);
            }
        }
    }

    private void ReportRefinement(SchemaRefinement refinement, string location) =>
        Add(new ParseIssue { Code = IssueCodes.Custom, Message = refinement.Message, InstanceLocation = location + refinement.Path });

    /// <summary>Exception text can name CLR types, so it's kept out of messages, which reach API clients.</summary>
    private static ImmutableDictionary<string, object?> Detail(JsonException exception) =>
        ImmutableDictionary<string, object?>.Empty.Add("detail", exception.Message);

    private void Add(ParseIssue issue) => (_issues ??= []).Add(issue);

    private string Pointer()
    {
        if (_path.Count == 0)
        {
            return string.Empty;
        }

        StringBuilder pointer = new();

        foreach (PathSegment segment in _path)
        {
            pointer.Append('/');

            if (segment.Name is null)
            {
                pointer.Append(segment.Index.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                pointer.Append(segment.Name.Replace("~", "~0", System.StringComparison.Ordinal).Replace("/", "~1", System.StringComparison.Ordinal));
            }
        }

        return pointer.ToString();
    }

    private readonly record struct PathSegment(string? Name, int Index);

    private sealed record PendingRefinement(SchemaRefinement Refinement, JsonElement Value, string Location);
}
