using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Json.Model;

namespace Zorg.Json.Validation;

internal sealed class ValidationContext
{
    private const int MaxDepth = 512;

    /// <summary>Union branches re-evaluate the same value, which is exponential for a recursive schema.</summary>
    private const int MaxSpeculativeSteps = 20_000_000;

    private readonly List<PathSegment> _path;
    private readonly List<Step> _steps;
    private readonly Limits _limits;
    private readonly bool _isFork;
    private List<ParseIssue>? _issues;
    private List<PendingRefinement>? _pending;

    public ValidationContext()
        : this([], [], new Limits(), isFork: false)
    {
    }

    private ValidationContext(List<PathSegment> path, List<Step> steps, Limits limits, bool isFork)
    {
        _path = path;
        _steps = steps;
        _limits = limits;
        _isFork = isFork;
    }

    public int IssueCount => _issues?.Count ?? 0;

    public bool HasIssues => IssueCount > 0;

    public bool HasPendingRefinements => _pending is not null;

    /// <summary>Set once evaluation stopped early. A fork's verdict then means nothing, so <c>not</c> mustn't invert it.</summary>
    public bool LimitExceeded => _limits.Exceeded;

    public int RewriteCount { get; private set; }

    public int Depth { get; private set; }

    public ImmutableArray<ParseIssue> ToIssues() => _issues is null ? [] : [.. _issues];

    public void Rewritten() => RewriteCount++;

    public bool Enter()
    {
        if (++Depth > MaxDepth)
        {
            Depth--;
            _limits.Exceeded = true;
            Report(IssueCodes.InvalidValue, "$ref", "invalid_value.depth", "maximum", MaxDepth);
            return false;
        }

        if (_isFork && ++_limits.SpeculativeSteps > MaxSpeculativeSteps)
        {
            Depth--;
            _limits.Exceeded = true;
            Report(IssueCodes.InvalidValue, "anyOf", "invalid_value.complex", "maximum", MaxSpeculativeSteps);
            return false;
        }

        return true;
    }

    public void Leave() => Depth--;

    public void Push(string name) => _path.Add(new PathSegment(name, 0));

    public void Push(int index) => _path.Add(new PathSegment(null, index));

    public void Pop() => _path.RemoveAt(_path.Count - 1);

    /// <summary>Enters a keyword with no name or index under it, such as <c>items</c>.</summary>
    public void Walk(string keyword) => _steps.Add(new Step(keyword, null, -1, null));

    /// <summary>Enters a keyword's named entry, such as <c>properties</c> and <c>a</c>.</summary>
    public void Walk(string keyword, string name) => _steps.Add(new Step(keyword, name, -1, null));

    /// <summary>Enters a keyword's indexed entry, such as <c>allOf</c> and <c>0</c>.</summary>
    public void Walk(string keyword, int index) => _steps.Add(new Step(keyword, null, index, null));

    /// <summary>Enters a <c>$ref</c>. Schema locations continue from <paramref name="target"/>.</summary>
    public void WalkReference(string target) => _steps.Add(new Step("$ref", null, -1, target));

    public void Unwalk() => _steps.RemoveAt(_steps.Count - 1);

    /// <summary>Evaluates a branch whose issues only count if the caller says so. Shares the path.</summary>
    public ValidationContext Fork() => new(_path, _steps, _limits, isFork: true) { Depth = Depth };

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
            EvaluationPath = EvaluationPath(keyword),
            SchemaLocation = SchemaLocation(keyword),
            Keyword = keyword,
            Parameters = ImmutableDictionary<string, object?>.Empty.Add(parameter, value),
        });

    /// <summary>The <c>false</c> schema is the failing schema itself, so no keyword is added to the paths.</summary>
    public void ReportFalse() =>
        Add(new ParseIssue
        {
            Code = IssueCodes.InvalidValue,
            Message = Messages.Get("invalid_value.false"),
            InstanceLocation = Pointer(),
            EvaluationPath = EvaluationPath(null),
            SchemaLocation = SchemaLocation(null),
            Keyword = "false",
            Parameters = ImmutableDictionary<string, object?>.Empty.Add("expected", null),
        });

    public void ReportRefinement(SchemaRefinement refinement) =>
        ReportRefinement(refinement, Pointer(), EvaluationPath(null), SchemaLocation(null));

    public void ReportInvalidJson(Exception exception) =>
        Add(new ParseIssue
        {
            Code = IssueCodes.InvalidJson,
            Message = Messages.Get(IssueCodes.InvalidJson),
            InstanceLocation = string.Empty,
            Parameters = Detail(exception),
        });

    public void ReportDeserializationFailed(Exception exception) =>
        ReportDeserializationFailed(exception, Pointer(), EvaluationPath(null), SchemaLocation(null));

    public void Defer(SchemaRefinement refinement, JsonElement value) =>
        (_pending ??= []).Add(new PendingRefinement(refinement, value.Clone(), Pointer(), EvaluationPath(null), SchemaLocation(null)));

    public async ValueTask RunPendingAsync(CancellationToken cancellationToken)
    {
        if (_pending is null)
        {
            return;
        }

        foreach (PendingRefinement pending in _pending)
        {
            try
            {
                if (!await pending.Refinement.CheckAsync(pending.Value, cancellationToken).ConfigureAwait(false))
                {
                    ReportRefinement(pending.Refinement, pending.Location, pending.EvaluationPath, pending.SchemaLocation);
                }
            }
            catch (JsonException exception)
            {
                ReportDeserializationFailed(exception, pending.Location, pending.EvaluationPath, pending.SchemaLocation);
            }
        }
    }

    private void ReportDeserializationFailed(Exception exception, string location, string evaluationPath, string schemaLocation) =>
        Add(new ParseIssue
        {
            Code = IssueCodes.DeserializationFailed,
            Message = Messages.Get(IssueCodes.DeserializationFailed),
            InstanceLocation = location,
            EvaluationPath = evaluationPath,
            SchemaLocation = schemaLocation,
            Parameters = Detail(exception),
        });

    private void ReportRefinement(SchemaRefinement refinement, string location, string evaluationPath, string schemaLocation) =>
        Add(new ParseIssue
        {
            Code = IssueCodes.Custom,
            Message = refinement.Message,
            InstanceLocation = location + refinement.Path,
            EvaluationPath = evaluationPath,
            SchemaLocation = schemaLocation,
        });

    /// <summary>Exception text can name CLR types, so it's kept out of messages, which reach API clients.</summary>
    private static ImmutableDictionary<string, object?> Detail(Exception exception) =>
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

    private string EvaluationPath(string? keyword)
    {
        if ((_steps.Count == 0 && keyword is null) || !CanSpend(_steps.Count))
        {
            return string.Empty;
        }

        StringBuilder path = new();

        foreach (Step step in _steps)
        {
            step.AppendTo(path, fragment: false);
        }

        Append(path, keyword, fragment: false);
        return path.ToString();
    }

    private string SchemaLocation(string? keyword)
    {
        int start = _steps.Count - 1;

        while (start >= 0 && _steps[start].Target is null)
        {
            start--;
        }

        if (!CanSpend(_steps.Count - start))
        {
            return string.Empty;
        }

        StringBuilder location = new(start < 0 ? "#" : _steps[start].Target);

        for (int index = start + 1; index < _steps.Count; index++)
        {
            _steps[index].AppendTo(location, fragment: true);
        }

        Append(location, keyword, fragment: true);
        return location.ToString();
    }

    /// <summary>Turns a JSON Pointer, already escaped with <c>~</c>, into a URI fragment.</summary>
    internal static string Fragment(string pointer)
    {
        StringBuilder fragment = new("#");

        foreach (string token in pointer.Split('/').Skip(1))
        {
            Append(fragment, token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal), fragment: true);
        }

        return fragment.ToString();
    }

    /// <summary>Escapes a token for a JSON Pointer, and for a URI fragment when <paramref name="fragment"/> is set.</summary>
    internal static void Append(StringBuilder path, string? token, bool fragment)
    {
        if (token is null)
        {
            return;
        }

        path.Append('/');

        foreach (char character in token)
        {
            switch (character)
            {
                case '~':
                    path.Append("~0");
                    break;

                case '/':
                    path.Append("~1");
                    break;

                case >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.' or '_' or '$' or '!' or '&' or '\'' or '(' or ')' or '*' or '+' or ',' or ';' or '=' or ':' or '@':
                    path.Append(character);
                    break;

                default:
                    if (fragment)
                    {
                        foreach (byte value in Encoding.UTF8.GetBytes(character.ToString()))
                        {
                            path.Append('%').Append(value.ToString("X2", CultureInfo.InvariantCulture));
                        }
                    }
                    else
                    {
                        path.Append(character);
                    }

                    break;
            }
        }
    }

    private readonly record struct Step(string Keyword, string? Name, int Index, string? Target)
    {
        public void AppendTo(StringBuilder path, bool fragment)
        {
            Append(path, Keyword, fragment);

            if (Name is not null)
            {
                Append(path, Name, fragment);
            }
            else if (Index >= 0)
            {
                path.Append('/').Append(Index.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    /// <summary>Speculative branches can report millions of issues that are never shown, so their paths are built within a budget.</summary>
    private bool CanSpend(int segments) => !_isFork || (_limits.PathBudget -= segments + 1) >= 0;

    private sealed class Limits
    {
        public int SpeculativeSteps { get; set; }

        public bool Exceeded { get; set; }

        public long PathBudget { get; set; } = 2_000_000;
    }

    private readonly record struct PathSegment(string? Name, int Index);

    private sealed record PendingRefinement(SchemaRefinement Refinement, JsonElement Value, string Location, string EvaluationPath, string SchemaLocation);
}
