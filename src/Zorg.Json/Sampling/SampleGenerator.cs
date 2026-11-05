using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Zorg.Json.Model;
using Zorg.Json.Validation;

namespace Zorg.Json.Sampling;

/// <summary>
/// Searches for a conforming document. Each node gives a lazy stream of candidates, from its literals and then from
/// its <see cref="SampleView"/>s, and only candidates that pass <see cref="ValidationPlan"/> go up a level. The work is
/// counted rather than timed, so that the result is the same on every run.
/// </summary>
internal sealed class SampleGenerator
{
    /// <summary>How many candidates of one kind are tried, and how many more when a list needs them to differ.</summary>
    private const int Breadth = 16;

    /// <summary>How many times each property or item of a rejected document is swapped for its next candidate.</summary>
    private const int Rounds = 3;

    private const int MaxViews = 32;

    /// <summary>A plan is built recursively too, on top of this stack, so the limit leaves it most of the room.</summary>
    private const int MaxDepth = 64;

    /// <summary>Beyond this, <c>minItems</c> and <c>minProperties</c> count as unsatisfiable here.</summary>
    private const int MaxCollection = 10_000;

    /// <summary>After this many checks everything is left out that can be, so that wide recursive schemas stay small.</summary>
    private const int SoftWorkLimit = 20_000;

    /// <summary>After this many rejected candidates and merges the search stops.</summary>
    private const int HardWorkLimit = 40_000;

    /// <summary>After this many characters of JSON have been built, the search stops, so that nested minimums can't exhaust memory.</summary>
    private const long MaxProduced = 8_000_000;

    /// <summary>
    /// After this many evaluation steps over all the candidates that were rejected, the search stops. Each check has a budget
    /// of its own, and a schema of nested unions can use it up in full. A rejection builds an issue for each step, which
    /// costs memory, while a candidate that passes costs none.
    /// </summary>
    private const long MaxSteps = 50_000;

    private const int RootAttempts = 8;

    /// <summary>What a typed <see cref="DateTime"/>, <see cref="TimeOnly"/> or <see cref="TimeSpan"/> reads. Their nodes are plain strings.</summary>
    private static readonly string[] TypedStrings = ["2024-01-15T12:00:00Z", "12:00:00"];

    private static readonly JsonDocumentOptions ReaderOptions = new() { MaxDepth = int.MaxValue };
    private static readonly JsonElement Null = JsonElement.Parse("null");

    private readonly ISchema _schema;
    private readonly SchemaNode _root;
    private readonly bool _includeOptional;
    private readonly int _maxRecursion;
    private readonly int _itemCount;
    private readonly SampleViews _views = new();
    private readonly Dictionary<SchemaNode, ValidationPlan> _plans = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<SchemaNode, JsonElement> _stopped = new(ReferenceEqualityComparer.Instance);
    private int _checks;
    private int _rejected;
    private long _produced;
    private long _steps;
    private bool _exhausted;

    public SampleGenerator(ISchema schema, SampleOptions options)
    {
        _schema = schema;
        _root = schema.ToNode();
        _includeOptional = options.IncludeOptionalProperties;
        _maxRecursion = Math.Max(0, options.MaxRecursionDepth);
        _itemCount = Math.Clamp(options.ArrayItemCount, 0, MaxCollection);
    }

    private bool OutOfWork => _rejected + _views.Merges > HardWorkLimit || _produced > MaxProduced || _steps > MaxSteps || _exhausted;

    public SampleResult Run()
    {
        _ = Plan(_root);

        Attempt attempt = new();
        JsonElement? closest = null;
        ImmutableArray<ParseIssue> closestIssues = [];
        int tries = 0;

        foreach (JsonElement candidate in Valid(_root, Walk.Start, Breadth, attempt))
        {
            ImmutableArray<ParseIssue> issues = Finish(candidate, out bool deferred);

            if (issues.IsEmpty)
            {
                return new SampleResult(candidate, []);
            }

            if (deferred)
            {
                return new SampleResult(candidate, issues);
            }

            if (Repair(candidate, issues) is { } repaired)
            {
                return new SampleResult(repaired, []);
            }

            if (closest is null)
            {
                closest = candidate;
                closestIssues = issues;
            }

            if (++tries == RootAttempts)
            {
                break;
            }
        }

        if (closest is { } value)
        {
            return new SampleResult(value, closestIssues);
        }

        // A refinement converts too, so that a typed date can also fail a candidate before it gets here.
        JsonElement best = attempt.Best ?? Null;
        ImmutableArray<ParseIssue> remaining = Finish(best, out _);
        return Repair(best, remaining) is { } fixedUp ? new SampleResult(fixedUp, []) : new SampleResult(best, remaining);
    }

    private static IEnumerable<JsonTypes> Kinds(SchemaNode view, bool stopping)
    {
        bool isObject = !view.Properties.IsEmpty || !view.Required.IsEmpty || !view.PatternProperties.IsEmpty || view.AdditionalProperties is not null
            || view.PropertyNames is not null || !view.DependentRequired.IsEmpty || view.MinProperties is not null || view.MaxProperties is not null;
        bool isArray = !view.PrefixItems.IsEmpty || view.Items is not null || view.Contains is not null || view.MinItems is not null
            || view.MaxItems is not null || view.UniqueItems is not null;
        bool isNumber = view.Minimum is not null || view.Maximum is not null || view.ExclusiveMinimum is not null || view.ExclusiveMaximum is not null || view.MultipleOf is not null;
        bool isString = view.MinLength is not null || view.MaxLength is not null || view.Pattern is not null || (view.Format is { } format && StringFormats.IsKnown(format));

        JsonTypes[] hinted = [.. new[] { (isObject, JsonTypes.Object), (isArray, JsonTypes.Array), (isNumber, JsonTypes.Number), (isString, JsonTypes.String) }
            .Where(hint => hint.Item1)
            .Select(hint => hint.Item2)];

        // Numbers come before strings so that a [Flags] enum, whose type is both, reads its 0.
        JsonTypes[] first = stopping && view.Types.HasFlag(JsonTypes.Null) ? [JsonTypes.Null] : [];
        JsonTypes[] order = [.. first, .. hinted, JsonTypes.Object, JsonTypes.Array, JsonTypes.Number, JsonTypes.String, JsonTypes.Boolean, JsonTypes.Null];
        JsonTypes allowed = view.Types;

        return order.Distinct().Where(kind =>
            allowed == JsonTypes.None || allowed.HasFlag(kind) || (kind == JsonTypes.Number && allowed.HasFlag(JsonTypes.Integer)));
    }

    /// <summary>Literals in the order of preference. A value of an enum that's not null comes first.</summary>
    private static IEnumerable<JsonElement> Literals(SchemaNode view)
    {
        if (view.Const is { } constant)
        {
            yield return constant;
        }

        if (view.Default is { } value)
        {
            yield return value;
        }

        foreach (JsonElement example in view.Examples)
        {
            yield return example;
        }

        foreach (JsonElement allowed in view.Enum.OrderBy(allowed => allowed.ValueKind == JsonValueKind.Null))
        {
            yield return allowed;
        }
    }

    private static List<(SchemaNode Node, int Count)> Wanted(SchemaNode view)
    {
        List<(SchemaNode, int)> wanted = [];

        if (view.Contains is { } contains && (view.MinContains ?? 1) > 0)
        {
            wanted.Add((contains, view.MinContains ?? 1));
        }

        foreach (SchemaNode residue in view.AllOf)
        {
            if (residue.Contains is { } extra && (residue.MinContains ?? 1) > 0)
            {
                wanted.Add((extra, residue.MinContains ?? 1));
            }
        }

        return wanted;
    }

    private ValidationPlan Plan(SchemaNode node)
    {
        if (!_plans.TryGetValue(node, out ValidationPlan? plan))
        {
            plan = ValidationPlan.Create(node);
            _plans.Add(node, plan);
        }

        return plan;
    }

    private bool Passes(SchemaNode node, JsonElement value, out int issues)
    {
        _checks++;
        ValidationContext context = new(value);
        Plan(node).Run(value, context);
        issues = context.IssueCount;

        if (issues > 0)
        {
            _steps += context.Steps;
        }

        _exhausted |= context.LimitExceeded;
        _rejected += Math.Min(issues, 1);
        return issues == 0;
    }

    private bool Stopping(Walk walk) => walk.Stopping || _checks + _views.Merges > SoftWorkLimit;

    /// <summary>The full verdict on a document, as parsing would give it.</summary>
    private ImmutableArray<ParseIssue> Finish(JsonElement sample, out bool deferred)
    {
        ValidationContext context = new(sample);
        JsonElement value = Plan(_root).Run(sample, context);
        deferred = false;

        if (!context.HasIssues && context.HasPendingRefinements)
        {
            deferred = true;
            return [new ParseIssue { Code = IssueCodes.Custom, Message = Messages.Get("sample.async_refinement"), InstanceLocation = string.Empty }];
        }

        return _schema is ISampleTarget target ? target.FinishSample(value, context) : context.ToIssues();
    }

    /// <summary>
    /// A plain string may turn out to be a typed date or time, which the node doesn't show. Conversion stops at the
    /// first failure, so a string has been fixed when the failure moves on.
    /// </summary>
    private JsonElement? Repair(JsonElement sample, ImmutableArray<ParseIssue> issues)
    {
        if (issues.IsEmpty || issues.Any(issue => issue.Code != IssueCodes.DeserializationFailed))
        {
            return null;
        }

        const string Quoted = $"\"{SampleStrings.Plain}\"";
        string text = sample.GetRawText();
        string? failure = Detail(issues);
        int at = 0;

        while ((at = text.IndexOf(Quoted, at, StringComparison.Ordinal)) >= 0)
        {
            int after = at + Quoted.Length;

            if (after < text.Length && text[after] == ':')
            {
                at = after;
                continue;
            }

            foreach (string replacement in TypedStrings)
            {
                string changed = $"{text[..at]}\"{replacement}\"{text[after..]}";
                JsonElement element = JsonElement.Parse(changed, ReaderOptions);
                ImmutableArray<ParseIssue> remaining = Finish(element, out _);

                if (remaining.IsEmpty)
                {
                    return element;
                }

                if (Detail(remaining) != failure && remaining.All(issue => issue.Code == IssueCodes.DeserializationFailed))
                {
                    text = changed;
                    failure = Detail(remaining);
                    break;
                }
            }

            at++;
        }

        return null;
    }

    /// <summary>The message up to its position, which moves with every replacement, so that only the path counts.</summary>
    private static string? Detail(ImmutableArray<ParseIssue> issues) =>
        issues.Length > 0 && issues[0].Parameters.TryGetValue("detail", out object? detail) ? (detail as string)?.Split(" | ")[0] : null;

    private (JsonElement? Value, bool IsValid) Sample(SchemaNode node, Walk walk)
    {
        bool stopping = Stopping(walk);

        if (stopping && _stopped.TryGetValue(node, out JsonElement known))
        {
            return (known, true);
        }

        Attempt attempt = new();

        foreach (JsonElement value in Valid(node, walk, Breadth, attempt))
        {
            if (stopping)
            {
                _stopped[node] = value;
            }

            return (value, true);
        }

        return (attempt.Best, false);
    }

    private IEnumerable<JsonElement> Valid(SchemaNode node, Walk walk, int breadth, Attempt attempt)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (JsonElement candidate in Candidates(node, walk, breadth))
        {
            if (!seen.Add(candidate.GetRawText()))
            {
                continue;
            }

            if (Passes(node, candidate, out int issues))
            {
                yield return candidate;
            }
            else
            {
                attempt.Note(candidate, issues);
            }

            if (OutOfWork)
            {
                yield break;
            }
        }
    }

    private IEnumerable<JsonElement> Candidates(SchemaNode node, Walk walk, int breadth)
    {
        if (walk.Depth > MaxDepth)
        {
            yield break;
        }

        IEnumerable<SampleView> views = _views.Of(node).Take(MaxViews);

        // Past the depth, a branch that doesn't recurse comes first, which is also how a nullable reference ends.
        foreach (SampleView view in Stopping(walk) ? views.OrderBy(view => view.Targets.Length > 0) : views)
        {
            if (OutOfWork)
            {
                yield break;
            }

            if (walk.Enter(view.Targets, _maxRecursion) is not { } inner)
            {
                continue;
            }

            foreach (JsonElement literal in Literals(view.Node))
            {
                yield return literal;
            }

            if (view.Node.Const is not null || !view.Node.Enum.IsEmpty)
            {
                continue;
            }

            foreach (JsonTypes kind in Kinds(view.Node, Stopping(inner)))
            {
                foreach (JsonElement candidate in Generated(kind, view.Node, inner, breadth))
                {
                    yield return candidate;
                }
            }
        }
    }

    private IEnumerable<JsonElement> Generated(JsonTypes kind, SchemaNode view, Walk walk, int breadth)
    {
        switch (kind)
        {
            case JsonTypes.Object:
                return Objects(view, walk, breadth);

            case JsonTypes.Array:
                return Arrays(view, walk, breadth);

            case JsonTypes.String:
                return SampleStrings.Candidates(view, breadth).Select(JsonStrings.ToElement);

            case JsonTypes.Number:
                bool integer = view.Types.HasFlag(JsonTypes.Integer) && !view.Types.HasFlag(JsonTypes.Number);
                return SampleNumbers.Candidates(view, integer, breadth).Select(text => JsonElement.Parse(text));

            case JsonTypes.Boolean:
                return [JsonElement.Parse("false"), JsonElement.Parse("true")];

            default:
                return [Null];
        }
    }

    private IEnumerable<JsonElement> Arrays(SchemaNode view, Walk walk, int breadth)
    {
        int minimum = view.MinItems ?? 0;
        int maximum = view.MaxItems ?? int.MaxValue;

        if (view.Items?.BooleanValue == false)
        {
            maximum = Math.Min(maximum, view.PrefixItems.Length);
        }

        List<(SchemaNode Node, int Count)> wanted = Wanted(view);
        long wide = Math.Max(minimum, wanted.Sum(want => (long)want.Count));

        if (wide > maximum && wanted.Count > 1)
        {
            // Too few places for each contains to have its own, so that one item has to satisfy them all.
            int shared = wanted.Max(want => want.Count);
            wanted = [(SampleViews.All(wanted.Select(want => want.Node))!, shared)];
            wide = Math.Max(minimum, shared);
        }

        if (wide > MaxCollection)
        {
            yield break;
        }

        int floor = (int)wide;

        int preferred = floor;

        if (!Stopping(walk) && (view.Items is not null || !view.PrefixItems.IsEmpty))
        {
            preferred = Math.Max(floor, Math.Max(view.PrefixItems.Length, view.Items is null ? 0 : _itemCount));
        }

        int rounds = Rounds + breadth - Breadth;
        List<int> lengths = [Math.Min(preferred, maximum), Math.Min(floor, maximum)];
        lengths.AddRange(Enumerable.Range(floor + 1, rounds).Where(length => length <= maximum));

        IReadOnlyList<SchemaNode>? firstNodes = null;
        IReadOnlyList<JsonElement>? firstItems = null;

        foreach (int length in lengths.Distinct())
        {
            (List<SchemaNode> nodes, List<JsonElement> items) = Fill(view, length, wanted, walk, breadth + length);
            firstNodes ??= nodes;
            firstItems ??= items;
            yield return JsonStrings.ToArray(items);
        }

        foreach (List<JsonElement> variation in Variations(firstNodes!, firstItems!, walk, breadth))
        {
            yield return JsonStrings.ToArray(variation);
        }
    }

    private (List<SchemaNode> Nodes, List<JsonElement> Items) Fill(SchemaNode view, int length, List<(SchemaNode Node, int Count)> wanted, Walk walk, int breadth)
    {
        List<SchemaNode> nodes = [];
        List<JsonElement> items = [];
        bool distinct = view.UniqueItems == true;
        int wantIndex = 0;
        int wantUsed = 0;
        SchemaNode? previousSlot = null;
        SchemaNode? previousWant = null;
        SchemaNode? previousNode = null;

        for (int index = 0; index < length && !OutOfWork; index++)
        {
            SchemaNode slot = index < view.PrefixItems.Length ? view.PrefixItems[index] : view.Items ?? SchemaNode.True;
            SchemaNode? want = wantIndex < wanted.Count ? wanted[wantIndex].Node : null;

            if (want is not null && ++wantUsed == wanted[wantIndex].Count)
            {
                wantIndex++;
                wantUsed = 0;
            }

            bool repeats = ReferenceEquals(slot, previousSlot) && ReferenceEquals(want, previousWant);
            SchemaNode node = repeats ? previousNode! : SampleViews.All([slot, want]) ?? SchemaNode.True;
            bool avoidsContains = want is null && view.MaxContains is not null && view.Contains is not null;

            if (repeats && !distinct && !avoidsContains && items.Count == index)
            {
                nodes.Add(node);
                items.Add(items[^1]);
                Charge(items[^1], 0);
                continue;
            }

            (previousSlot, previousWant, previousNode) = (slot, want, node);

            if (Pick(node, view, walk, breadth, items, distinct, avoidsContains) is not { } item)
            {
                break;
            }

            nodes.Add(node);
            items.Add(item);
            Charge(item, 0);
        }

        return (nodes, items);
    }

    private JsonElement? Pick(SchemaNode node, SchemaNode view, Walk walk, int breadth, List<JsonElement> items, bool distinct, bool avoidsContains)
    {
        if (!distinct && !avoidsContains)
        {
            return Sample(node, walk) is { Value: { } value } ? value : null;
        }

        Attempt attempt = new();

        foreach (JsonElement value in Valid(node, walk, breadth, attempt))
        {
            if ((!distinct || !items.Any(item => JsonEquality.AreEqual(item, value)))
                && (!avoidsContains || !Passes(view.Contains!, value, out _)))
            {
                return value;
            }
        }

        return attempt.Best;
    }

    private IEnumerable<JsonElement> Objects(SchemaNode view, Walk walk, int breadth)
    {
        bool stopping = Stopping(walk);
        bool[] choices = _includeOptional && !stopping && view.Properties.Any(property => !view.Required.Contains(property.Name)) ? [true, false] : [false];
        List<SchemaNode>? firstNodes = null;
        List<JsonElement>? firstValues = null;
        List<string>? firstNames = null;

        if ((view.MinProperties ?? 0) > MaxCollection)
        {
            yield break;
        }

        foreach (bool optional in choices)
        {
            List<string> names = [];
            List<SchemaNode> nodes = [];
            List<JsonElement> values = [];

            foreach (string name in Names(view, optional))
            {
                Add(view, walk, name, view.Required.Contains(name), names, nodes, values);
            }

            FillProperties(view, walk, breadth, names, nodes, values);

            firstNames ??= names;
            firstNodes ??= nodes;
            firstValues ??= values;
            yield return JsonStrings.ToObject(names.Zip(values, KeyValuePair.Create));
        }

        foreach (List<JsonElement> variation in Variations(firstNodes!, firstValues!, walk, breadth))
        {
            yield return JsonStrings.ToObject(firstNames!.Zip(variation, KeyValuePair.Create));
        }
    }

    private bool Add(SchemaNode view, Walk walk, string name, bool needed, List<string> names, List<SchemaNode> nodes, List<JsonElement> values)
    {
        SchemaNode node = _views.ForName(view, name);
        (JsonElement? value, bool valid) = Sample(node, walk);

        if (value is not { } found || (!valid && !needed))
        {
            return false;
        }

        names.Add(name);
        nodes.Add(node);
        values.Add(found);
        Charge(found, name.Length);
        return true;
    }

    private void Charge(JsonElement value, int nameLength) => _produced += value.GetRawText().Length + nameLength + 1;

    /// <summary>Declared properties in order, then the rest. Dependencies come along, and <c>maxProperties</c> holds.</summary>
    private List<string> Names(SchemaNode view, bool optional)
    {
        HashSet<string> chosen = new(StringComparer.Ordinal);
        int maximum = view.MaxProperties ?? int.MaxValue;

        foreach (string name in view.Required)
        {
            chosen.UnionWith(WithDependencies(view, name));
        }

        if (optional)
        {
            foreach (SchemaProperty property in view.Properties)
            {
                HashSet<string> needed = WithDependencies(view, property.Name);

                if (needed.Except(chosen).Count() + chosen.Count <= maximum && AllowsName(view, property.Name))
                {
                    chosen.UnionWith(needed);
                }
            }
        }

        List<string> ordered = [.. view.Properties.Select(property => property.Name).Where(chosen.Contains)];
        ordered.AddRange(view.Required.Concat(chosen).Distinct(StringComparer.Ordinal).Where(name => !ordered.Contains(name)));
        return ordered;
    }

    private static HashSet<string> WithDependencies(SchemaNode view, string name)
    {
        HashSet<string> names = new(StringComparer.Ordinal) { name };
        Queue<string> pending = new([name]);

        while (pending.TryDequeue(out string? next))
        {
            foreach ((string key, ImmutableArray<string> dependencies) in view.DependentRequired)
            {
                if (key == next)
                {
                    foreach (string dependency in dependencies.Where(names.Add))
                    {
                        pending.Enqueue(dependency);
                    }
                }
            }
        }

        return names;
    }

    private bool AllowsName(SchemaNode view, string name) =>
        view.PropertyNames is null || Passes(view.PropertyNames, JsonStrings.ToElement(name), out _);

    private void FillProperties(SchemaNode view, Walk walk, int breadth, List<string> names, List<SchemaNode> nodes, List<JsonElement> values)
    {
        int wanted = view.MinProperties ?? 0;

        if (names.Count >= wanted)
        {
            return;
        }

        foreach (string name in view.Properties.Select(property => property.Name).Concat(ExtraNames(view, walk, breadth + wanted)))
        {
            if (!names.Contains(name) && AllowsName(view, name))
            {
                Add(view, walk, name, needed: false, names, nodes, values);
            }

            if (names.Count >= wanted || OutOfWork)
            {
                return;
            }
        }
    }

    private IEnumerable<string> ExtraNames(SchemaNode view, Walk walk, int breadth)
    {
        foreach (SchemaProperty property in view.PatternProperties)
        {
            foreach (string name in PatternSampler.Generate(property.Name, 0, int.MaxValue, breadth))
            {
                yield return name;
            }
        }

        if (view.AdditionalProperties?.BooleanValue == false)
        {
            yield break;
        }

        if (view.PropertyNames is { } names)
        {
            foreach (JsonElement name in Valid(names, walk, breadth, new Attempt()))
            {
                if (name.ValueKind == JsonValueKind.String)
                {
                    yield return name.GetString()!;
                }
            }
        }

        for (int index = 1; index <= breadth; index++)
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"property{index}");
        }
    }

    /// <summary>Changes one item at a time to its next valid candidate, for a document the parts of which pass but which doesn't.</summary>
    private IEnumerable<List<JsonElement>> Variations(IReadOnlyList<SchemaNode> nodes, IReadOnlyList<JsonElement> values, Walk walk, int breadth)
    {
        int rounds = Rounds + breadth - Breadth;
        List<JsonElement>?[] alternatives = new List<JsonElement>?[nodes.Count];

        for (int round = 1; round <= rounds; round++)
        {
            for (int index = 0; index < nodes.Count; index++)
            {
                if (OutOfWork)
                {
                    yield break;
                }

                alternatives[index] ??= [.. Valid(nodes[index], walk, breadth, new Attempt()).Take(rounds + 1)];

                if (round < alternatives[index]!.Count)
                {
                    List<JsonElement> changed = [.. values];
                    changed[index] = alternatives[index]![round];
                    yield return changed;
                }
            }
        }
    }

    /// <summary>The closest failure so far: the one with the fewest issues, and the earliest of those.</summary>
    private sealed class Attempt
    {
        private int _issues = int.MaxValue;

        public JsonElement? Best { get; private set; }

        public void Note(JsonElement candidate, int issues)
        {
            if (issues < _issues)
            {
                _issues = issues;
                Best = candidate;
            }
        }
    }

    /// <summary>Where the search is: how deep, and which references it's inside.</summary>
    private sealed record Walk(Walk? Parent, ImmutableArray<SchemaNode> Targets, bool Stopping, int Depth)
    {
        public static Walk Start { get; } = new(null, [], Stopping: false, Depth: 0);

        /// <summary>Gives null once a reference has repeated more than stopping allows, which only a required property can reach.</summary>
        public Walk? Enter(ImmutableArray<SchemaNode> targets, int maxRecursion)
        {
            bool stopping = Stopping;

            foreach (SchemaNode target in targets)
            {
                int count = Count(target);

                if (count > maxRecursion + 1)
                {
                    return null;
                }

                stopping |= count >= maxRecursion;
            }

            return new Walk(this, targets, stopping, Depth + 1);
        }

        private int Count(SchemaNode target)
        {
            int count = 0;

            for (Walk? walk = this; walk is not null; walk = walk.Parent)
            {
                foreach (SchemaNode seen in walk.Targets)
                {
                    if (ReferenceEquals(seen, target))
                    {
                        count++;
                    }
                }
            }

            return count;
        }
    }
}
