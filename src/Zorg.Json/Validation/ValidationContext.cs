using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Json.Model;

namespace Zorg.Json.Validation;

internal sealed class ValidationContext
{
    private const int MaxDepth = 512;

    /// <summary>The most issues a context keeps. The next one is replaced by a single <c>too_many_issues</c> issue and evaluation stops.</summary>
    internal const int MaxIssues = 100;

    /// <summary>The most issues that failed union branches keep, over a whole parse.</summary>
    internal const int RetainedIssueBudget = 20_000;

    /// <summary>The most property names an <c>unrecognized_keys</c> issue lists, in its message and its parameters.</summary>
    internal const int MaxListedKeys = 20;

    /// <summary>
    /// Union branches re-evaluate the same value, which is exponential for a recursive schema. The budget
    /// grows with the document, so that a small hostile one stops quickly and a large honest one has room.
    /// </summary>
    private const long BaseSpeculativeSteps = 100_000;
    private const long SpeculativeStepsPerByte = 16;

    /// <summary>
    /// The same bound over every step, speculative or not, because a schema of <c>$ref</c>s and <c>allOf</c>s costs
    /// exponential time without a single fork.
    /// </summary>
    private const long BaseTotalSteps = 500_000;
    private const long TotalStepsPerByte = 48;

    private readonly List<PathSegment> _path;
    private readonly List<Step> _steps;
    private readonly Limits _limits;
    private readonly bool _isFork;
    private List<ParseIssue>? _issues;
    private bool _stopped;
    private List<PendingRefinement>? _pending;

    public ValidationContext()
        : this([], [], Scaled(0), isFork: false)
    {
    }

    public ValidationContext(JsonElement document)
        : this([], [], Scaled(JsonMarshal.GetRawUtf8Value(document).Length), isFork: false)
    {
    }

    private static Limits Scaled(long bytes) => new()
    {
        MaxSpeculativeSteps = BaseSpeculativeSteps + (SpeculativeStepsPerByte * bytes),
        MaxTotalSteps = BaseTotalSteps + (TotalStepsPerByte * bytes),
    };

    private ValidationContext(List<PathSegment> path, List<Step> steps, Limits limits, bool isFork)
    {
        _path = path;
        _steps = steps;
        _limits = limits;
        _isFork = isFork;
    }

    /// <summary>
    /// What the caller sees. Once a limit was hit anywhere, the main context fails even if no branch said so: a fork's
    /// verdict means nothing then, and it mustn't turn into acceptance.
    /// </summary>
    public int IssueCount => (_issues?.Count ?? 0) + (LimitIssuePending ? 1 : 0);

    public bool HasIssues => IssueCount > 0;

    private bool LimitIssuePending => !_isFork && _limits.Exceeded && !_limits.Reported;

    /// <summary>Set once evaluation should stop: a limit was hit, or this context has all the issues it keeps.</summary>
    public bool Halted => _limits.Exceeded || _stopped;

    public bool HasPendingRefinements => _pending is not null;

    /// <summary>Set once evaluation stopped early, whichever context hit the limit.</summary>
    public bool LimitExceeded => _limits.Exceeded;

    /// <summary>The evaluation steps spent so far, whichever context spent them.</summary>
    public long Steps => _limits.TotalSteps;

    public int RewriteCount { get; private set; }

    public int Depth { get; private set; }

    public ImmutableArray<ParseIssue> ToIssues()
    {
        if (LimitIssuePending)
        {
            _limits.Reported = true;
            (_issues ??= []).Add(_limits.Issue!);
        }

        return _issues is null ? [] : [.. _issues];
    }

    /// <summary>
    /// The issues of a failed branch, for the union's own issue. A recursive schema over a large document can fail millions
    /// of branches, and keeping every tree costs memory in proportion to the work, so only the first
    /// <see cref="RetainedIssueBudget"/> issues of a parse are kept. A branch beyond that is listed with no issues.
    /// </summary>
    public ImmutableArray<ParseIssue> Retain(ValidationContext fork)
    {
        ImmutableArray<ParseIssue> issues = fork.ToIssues();
        _limits.RetainBudget -= issues.Length;
        return _limits.RetainBudget < 0 ? [] : issues;
    }

    public void Rewritten() => RewriteCount++;

    public bool Enter()
    {
        if (Halted)
        {
            return false;
        }

        if (++Depth > MaxDepth)
        {
            Depth--;
            Exceed(IssueCodes.InvalidValue, "$ref", "invalid_value.depth", "maximum", MaxDepth);
            return false;
        }

        if (++_limits.TotalSteps > _limits.MaxTotalSteps)
        {
            Depth--;
            Exceed(IssueCodes.InvalidValue, "$ref", "invalid_value.complex", "maximum", _limits.MaxTotalSteps);
            return false;
        }

        if (_isFork && ++_limits.SpeculativeSteps > _limits.MaxSpeculativeSteps)
        {
            Depth--;
            Exceed(IssueCodes.InvalidValue, "anyOf", "invalid_value.complex", "maximum", _limits.MaxSpeculativeSteps);
            return false;
        }

        return true;
    }

    /// <summary>A regular expression took too long. That ends the parse as a failure, like any other limit.</summary>
    public void ReportTimeout(string keyword, string pattern) =>
        Exceed(IssueCodes.InvalidFormat, keyword, "invalid_format.timeout", "pattern", pattern, pattern);

    /// <summary>
    /// A string or property name has a lone surrogate or bytes that aren't UTF-8, so a schema can't read it. That ends the
    /// parse as a failure: a branch that reads it can't count as "not a string" under <c>not</c> or <c>oneOf</c>.
    /// </summary>
    public void ReportUndecodable(string keyword) =>
        Exceed(IssueCodes.InvalidValue, keyword, "invalid_value.string", "expected", "string");

    /// <summary>A value would cost too much to process, such as a text that Unicode normalization handles in quadratic time. That ends the parse as a failure.</summary>
    public void ReportTooComplex(string keyword) =>
        Exceed(IssueCodes.InvalidValue, keyword, "invalid_value.complex", "maximum", _limits.MaxTotalSteps);

    /// <summary>Spends steps on work that isn't a schema evaluation, such as compiling a pattern the input supplied.</summary>
    public bool Spend(long steps)
    {
        if (Halted)
        {
            return false;
        }

        _limits.TotalSteps += steps;

        if (_limits.TotalSteps > _limits.MaxTotalSteps)
        {
            Exceed(IssueCodes.InvalidValue, "format", "invalid_value.complex", "maximum", _limits.MaxTotalSteps);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Records a limit. The issue goes to this context, and to the main one whichever fork hit it, so that no branch
    /// outcome can hide it.
    /// </summary>
    private void Exceed(string code, string keyword, string messageKey, string parameter, object? value, params object?[] messageArguments)
    {
        _limits.Exceeded = true;
        ParseIssue issue = Create(code, keyword, messageKey, parameter, value, messageArguments);
        _limits.Issue ??= issue;
        Add(issue, limit: true);
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
    public ValidationContext Fork() => new(_path, _steps, _limits, isFork: true) { Depth = Depth, _stopped = _stopped };

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
        Add(Create(code, keyword, messageKey, parameter, value, messageArguments));

    private ParseIssue Create(string code, string keyword, string messageKey, string parameter, object? value, object?[] messageArguments) =>
        new()
        {
            Code = code,
            Message = Messages.Get(messageKey, messageArguments),
            InstanceLocation = Pointer(),
            EvaluationPath = EvaluationPath(keyword),
            SchemaLocation = SchemaLocation(keyword),
            Keyword = keyword,
            Parameters = ImmutableDictionary<string, object?>.Empty.Add(parameter, value),
        };

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
            catch (MaterializationException exception)
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

    private void Add(ParseIssue issue, bool limit = false)
    {
        if (limit && !_isFork)
        {
            _limits.Reported = true;
        }
        else if (_stopped)
        {
            return;
        }
        else if ((_issues?.Count ?? 0) >= MaxIssues)
        {
            _stopped = true;
            issue = new ParseIssue
            {
                Code = IssueCodes.TooManyIssues,
                Message = Messages.Get("too_many_issues", MaxIssues),
                InstanceLocation = string.Empty,
                Parameters = ImmutableDictionary<string, object?>.Empty.Add("maximum", MaxIssues),
            };
        }

        (_issues ??= []).Add(issue);
    }

    private string Pointer()
    {
        if (_path.Count == 0)
        {
            return string.Empty;
        }

        if (_isFork)
        {
            // A long name or a deep path is copied into every issue, so a fork pays for the text it builds.
            long cost = _path.Count + 1;

            foreach (PathSegment segment in _path)
            {
                cost += segment.Name?.Length ?? 10;
            }

            if ((_limits.PathBudget -= cost) < 0)
            {
                return string.Empty;
            }
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

        for (int index = 0; index < token.Length; index++)
        {
            char character = token[index];

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
                        // A surrogate pair is one code point, so it's encoded together.
                        int length = char.IsHighSurrogate(character) && index + 1 < token.Length && char.IsLowSurrogate(token[index + 1]) ? 2 : 1;

                        foreach (byte value in Encoding.UTF8.GetBytes(token.Substring(index, length)))
                        {
                            path.Append('%').Append(value.ToString("X2", CultureInfo.InvariantCulture));
                        }

                        index += length - 1;
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
        public required long MaxSpeculativeSteps { get; init; }

        public required long MaxTotalSteps { get; init; }

        public long SpeculativeSteps { get; set; }

        public long TotalSteps { get; set; }

        public bool Exceeded { get; set; }

        /// <summary>The first limit that was hit, which the main context reports if no branch did.</summary>
        public ParseIssue? Issue { get; set; }

        public bool Reported { get; set; }

        public long PathBudget { get; set; } = 2_000_000;

        public long RetainBudget { get; set; } = RetainedIssueBudget;
    }

    private readonly record struct PathSegment(string? Name, int Index);

    private sealed record PendingRefinement(SchemaRefinement Refinement, JsonElement Value, string Location, string EvaluationPath, string SchemaLocation);
}
