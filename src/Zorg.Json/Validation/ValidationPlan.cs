using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Zorg.Json.Model;

namespace Zorg.Json.Validation;

/// <summary>
/// A <see cref="SchemaNode"/> prepared for repeated validation. Runs the pipeline
/// Coerce, Normalize, type check, constraints, refinements, and returns the value afterwards.
/// </summary>
internal sealed class ValidationPlan
{
    private readonly SchemaNode _node;
    private readonly Regex? _pattern;
    private readonly Func<string, bool>? _format;
    private readonly ExactNumber? _minimum;
    private readonly ExactNumber? _maximum;
    private readonly ExactNumber? _exclusiveMinimum;
    private readonly ExactNumber? _exclusiveMaximum;
    private readonly ExactNumber? _multipleOf;
    private readonly bool _checksNumbers;
    private readonly bool _readsStrings;
    private readonly ImmutableArray<ValidationPlan> _prefixItems;
    private readonly ValidationPlan? _items;
    private readonly ValidationPlan? _contains;
    private readonly FrozenDictionary<string, ValidationPlan> _properties;
    private readonly FrozenSet<string>? _namesIgnoringCase;
    private readonly ImmutableArray<(string Name, Regex Pattern, ValidationPlan Plan)> _patternProperties;
    private readonly ValidationPlan? _additionalProperties;
    private readonly string _itemsKeyword;
    private readonly string _additionalKeyword;
    private readonly string? _closedBy;
    private readonly ValidationPlan? _propertyNames;
    private readonly ImmutableArray<(string Name, ValidationPlan Plan)> _dependentSchemas;
    private readonly ImmutableArray<ValidationPlan> _allOf;
    private readonly ImmutableArray<ValidationPlan> _anyOf;
    private readonly ImmutableArray<ValidationPlan> _oneOf;
    private readonly FrozenDictionary<string, (ValidationPlan Plan, int Index)>? _discriminated;
    private readonly ValidationPlan? _not;
    private readonly ValidationPlan? _if;
    private readonly ValidationPlan? _then;
    private readonly ValidationPlan? _else;
    private readonly Dictionary<SchemaNode, ValidationPlan> _plans;
    private readonly ImmutableArray<(string Name, SchemaNode Node)> _defaults;
    private ValidationPlan? _target;
    private string? _targetLocation;

    private ValidationPlan(SchemaNode node, Dictionary<SchemaNode, ValidationPlan> plans)
    {
        plans.Add(node, this);
        _plans = plans;

        ValidationPlan? Single(SchemaNode? child) => child is null ? null : Get(child, plans);
        ImmutableArray<ValidationPlan> List(ImmutableArray<SchemaNode> children) => [.. children.Select(child => Get(child, plans))];

        _node = node;
        _pattern = node.Behavior.PatternRegex ?? (node.Pattern is null ? null : PatternCompiler.Compile(node.Pattern));
        _format = node.Behavior.FormatIsAnnotation ? null : StringFormats.Find(node.Format);
        _minimum = Number(node.Minimum);
        _maximum = Number(node.Maximum);
        _exclusiveMinimum = Number(node.ExclusiveMinimum);
        _exclusiveMaximum = Number(node.ExclusiveMaximum);
        _multipleOf = Number(node.MultipleOf);
        _checksNumbers = _minimum is not null || _maximum is not null || _exclusiveMinimum is not null || _exclusiveMaximum is not null
            || _multipleOf is not null || (node.Types.HasFlag(JsonTypes.Integer) && !node.Types.HasFlag(JsonTypes.Number));
        _readsStrings = node.Types.HasFlag(JsonTypes.String) || _pattern is not null || _format is not null || node.MinLength is not null
            || node.MaxLength is not null || !node.Behavior.Normalizers.IsEmpty;
        _prefixItems = List(node.PrefixItems);
        _items = Single(node.Items ?? node.UnevaluatedItems);
        _itemsKeyword = node.Items is null ? "unevaluatedItems" : "items";
        _contains = Single(node.Contains);
        _properties = node.Properties.Where(property => !property.OutputOnly).ToFrozenDictionary(property => property.Name, property => Get(property.Schema, plans), StringComparer.Ordinal);
        _namesIgnoringCase = node.Behavior.IgnoresNameCase
            ? node.Properties.Select(property => property.Name).ToFrozenSet(StringComparer.OrdinalIgnoreCase)
            : null;
        _patternProperties = [.. node.PatternProperties.Select(property => (property.Name, PatternCompiler.Compile(property.Name), Get(property.Schema, plans)))];
        _additionalProperties = Single(node.AdditionalProperties ?? node.UnevaluatedProperties);
        _additionalKeyword = node.AdditionalProperties is null ? "unevaluatedProperties" : "additionalProperties";
        _closedBy = node.AdditionalProperties?.BooleanValue == false ? "additionalProperties"
            : node.AdditionalProperties is null && node.UnevaluatedProperties?.BooleanValue == false ? "unevaluatedProperties"
            : null;
        _propertyNames = Single(node.PropertyNames);
        _dependentSchemas = [.. node.DependentSchemas.Select(property => (property.Name, Get(property.Schema, plans)))];
        _allOf = List(node.AllOf);
        _anyOf = List(node.AnyOf);
        _oneOf = List(node.OneOf);
        _discriminated = DiscriminatorKeys.TryBuild(node, out Dictionary<string, SchemaNode> branches)
            ? branches.ToFrozenDictionary(branch => branch.Key, branch => (Get(branch.Value, plans), IndexOf(node.OneOf, branch.Value)), StringComparer.Ordinal)
            : null;
        _not = Single(node.Not);
        _if = Single(node.If);
        _then = Single(node.Then);
        _else = Single(node.Else);
        _defaults = [.. node.Properties
            .Where(property => property.Schema.Behavior.FillDefault && property.Schema.Default is not null)
            .Select(property => (property.Name, property.Schema))];
    }

    private static int IndexOf(ImmutableArray<SchemaNode> nodes, SchemaNode node)
    {
        for (int index = 0; index < nodes.Length; index++)
        {
            if (ReferenceEquals(nodes[index], node))
            {
                return index;
            }
        }

        return 0;
    }

    public static ValidationPlan Create(SchemaNode node) => Get(node, new(ReferenceEqualityComparer.Instance));

    public JsonElement Run(JsonElement input, ValidationContext context)
    {
        if (_node.BooleanValue is { } allowed)
        {
            if (!allowed)
            {
                context.ReportFalse();
            }

            return input;
        }

        if (!context.Enter())
        {
            return input;
        }

        int issues = context.IssueCount;
        JsonElement value = Coerce(input, context);
        value = Normalize(value, context);

        if (CheckType(value, context))
        {
            bool settled = false;

            CheckValue(value, context);

            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    CheckString(value, context);
                    break;

                case JsonValueKind.Number:
                    CheckNumber(value, context);
                    break;

                case JsonValueKind.Array:
                    value = RunArray(value, context, out settled);
                    break;

                case JsonValueKind.Object:
                    value = RunObject(value, context, out settled);
                    break;
            }

            // A size bound that failed has already made this value invalid, and nothing below can undo that.
            if (!settled)
            {
                value = RunApplicators(value, context);

                if (context.IssueCount == issues)
                {
                    RunRefinements(value, context);
                }
            }
        }

        context.Leave();
        return value;
    }

    private static ValidationPlan Get(SchemaNode node, Dictionary<SchemaNode, ValidationPlan> plans) =>
        plans.TryGetValue(node, out ValidationPlan? plan) ? plan : new ValidationPlan(node, plans);

    private static ExactNumber? Number(JsonElement? value) => value is { } number ? ExactNumber.Parse(number.GetRawText()) : null;

    private static string TypeName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        _ => "null",
    };

    private static string TypeNames(JsonTypes types) =>
        string.Join(" or ", Enum.GetValues<JsonTypes>().Where(type => type != JsonTypes.None && types.HasFlag(type)).Select(JsonTypeNames.Name));

    private JsonElement Coerce(JsonElement value, ValidationContext context)
    {
        if (value.ValueKind != JsonValueKind.String || _node.Behavior.Coercion == SchemaCoercion.None)
        {
            return value;
        }

        string? text;

        try
        {
            text = value.GetString();
        }
        catch (InvalidOperationException)
        {
            return value;
        }

        if (_node.Behavior.Coercion == SchemaCoercion.Boolean)
        {
            if (text is "true" or "false")
            {
                context.Rewritten();
                return JsonElement.Parse(text);
            }

            return value;
        }

        if (text is not null && ExactNumber.TryParse(text, out _))
        {
            context.Rewritten();
            return JsonElement.Parse(text);
        }

        return value;
    }

    private JsonElement Normalize(JsonElement value, ValidationContext context)
    {
        if (_node.Behavior.Normalizers.IsEmpty || value.ValueKind != JsonValueKind.String || !TryGetString(value, context, out string? original))
        {
            return value;
        }

        string text = original;

        foreach (SchemaNormalizer normalizer in _node.Behavior.Normalizers)
        {
            text = normalizer.Apply(text);
        }

        if (text == original)
        {
            return value;
        }

        context.Rewritten();
        return JsonStrings.ToElement(text);
    }

    private bool CheckType(JsonElement value, ValidationContext context)
    {
        JsonTypes types = _node.Types;

        if (types == JsonTypes.None)
        {
            return true;
        }

        bool matches = value.ValueKind switch
        {
            JsonValueKind.Object => types.HasFlag(JsonTypes.Object),
            JsonValueKind.Array => types.HasFlag(JsonTypes.Array),
            JsonValueKind.String => types.HasFlag(JsonTypes.String),
            JsonValueKind.Number => types.HasFlag(JsonTypes.Number) || types.HasFlag(JsonTypes.Integer),
            JsonValueKind.True or JsonValueKind.False => types.HasFlag(JsonTypes.Boolean),
            _ => types.HasFlag(JsonTypes.Null),
        };

        if (!matches)
        {
            string expected = TypeNames(types);
            context.Report(IssueCodes.InvalidType, "type", IssueCodes.InvalidType, "expected", expected, expected, TypeName(value.ValueKind));
        }

        return matches;
    }

    private void CheckValue(JsonElement value, ValidationContext context)
    {
        if (_node.Const is { } constant && !JsonEquality.AreEqual(constant, value))
        {
            context.Report(IssueCodes.InvalidValue, "const", "invalid_value.const", "expected", constant.GetRawText(), constant.GetRawText());
        }

        if (!_node.Enum.IsEmpty && !_node.Enum.Any(allowed => JsonEquality.AreEqual(allowed, value)))
        {
            string[] values = [.. _node.Enum.Select(allowed => allowed.GetRawText())];
            context.Report(IssueCodes.InvalidValue, "enum", "invalid_value.enum", "values", values, string.Join(", ", values));
        }
    }

    private void CheckString(JsonElement value, ValidationContext context)
    {
        if (!_readsStrings || !TryGetString(value, context, out string? text))
        {
            return;
        }

        if (_node.MinLength is not null || _node.MaxLength is not null)
        {
            int length = JsonStrings.CodePointCount(text);

            if (length < _node.MinLength)
            {
                context.Report(IssueCodes.TooSmall, "minLength", "too_small.string", "minimum", _node.MinLength, _node.MinLength);
            }

            if (length > _node.MaxLength)
            {
                context.Report(IssueCodes.TooBig, "maxLength", "too_big.string", "maximum", _node.MaxLength, _node.MaxLength);
            }
        }

        if (_pattern is not null)
        {
            CheckPattern(text, context);
        }

        if (_format is not null && !_format(text))
        {
            context.Report(IssueCodes.InvalidFormat, "format", "invalid_format.format", "format", _node.Format, _node.Format);
        }
    }

    private void CheckPattern(string text, ValidationContext context)
    {
        try
        {
            if (!_pattern!.IsMatch(text))
            {
                context.Report(IssueCodes.InvalidFormat, "pattern", "invalid_format.pattern", "pattern", _node.Pattern, _node.Pattern);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            context.Report(IssueCodes.InvalidFormat, "pattern", "invalid_format.timeout", "pattern", _node.Pattern, _node.Pattern);
        }
    }

    private static bool TryGetString(JsonElement value, ValidationContext context, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? text)
    {
        try
        {
            text = value.GetString()!;
            return true;
        }
        catch (InvalidOperationException)
        {
            text = null;
            context.Report(IssueCodes.InvalidValue, "type", "invalid_value.string", "expected", "string");
            return false;
        }
    }

    private void CheckNumber(JsonElement value, ValidationContext context)
    {
        string text = value.GetRawText();

        if (_node.Behavior.NumberCheck is { } fits && !fits(text))
        {
            string expected = _node.Behavior.NumberType!;
            context.Report(IssueCodes.InvalidType, "type", IssueCodes.InvalidType, "expected", expected, expected, text);
            return;
        }

        if (!_checksNumbers)
        {
            return;
        }

        ExactNumber number = ExactNumber.Parse(text);

        if (_node.Types.HasFlag(JsonTypes.Integer) && !_node.Types.HasFlag(JsonTypes.Number) && !number.IsInteger)
        {
            context.Report(IssueCodes.InvalidType, "type", IssueCodes.InvalidType, "expected", "integer", "integer", "number");
            return;
        }

        if (_minimum is { } minimum && number.CompareTo(minimum) < 0)
        {
            string limit = _node.Minimum!.Value.GetRawText();
            context.Report(IssueCodes.TooSmall, "minimum", "too_small.number", "minimum", limit, limit);
        }

        if (_maximum is { } maximum && number.CompareTo(maximum) > 0)
        {
            string limit = _node.Maximum!.Value.GetRawText();
            context.Report(IssueCodes.TooBig, "maximum", "too_big.number", "maximum", limit, limit);
        }

        if (_exclusiveMinimum is { } exclusiveMinimum && number.CompareTo(exclusiveMinimum) <= 0)
        {
            string limit = _node.ExclusiveMinimum!.Value.GetRawText();
            context.Report(IssueCodes.TooSmall, "exclusiveMinimum", "too_small.exclusive", "minimum", limit, limit);
        }

        if (_exclusiveMaximum is { } exclusiveMaximum && number.CompareTo(exclusiveMaximum) >= 0)
        {
            string limit = _node.ExclusiveMaximum!.Value.GetRawText();
            context.Report(IssueCodes.TooBig, "exclusiveMaximum", "too_big.exclusive", "maximum", limit, limit);
        }

        if (_multipleOf is { } multipleOf && !number.IsMultipleOf(multipleOf))
        {
            string divisor = _node.MultipleOf!.Value.GetRawText();
            context.Report(IssueCodes.NotMultipleOf, "multipleOf", IssueCodes.NotMultipleOf, "divisor", divisor, divisor);
        }
    }

    private JsonElement RunArray(JsonElement value, ValidationContext context, out bool settled)
    {
        int length = value.GetArrayLength();
        settled = false;

        if (length < _node.MinItems)
        {
            context.Report(IssueCodes.TooSmall, "minItems", "too_small.array", "minimum", _node.MinItems, _node.MinItems);
        }

        if (length > _node.MaxItems)
        {
            context.Report(IssueCodes.TooBig, "maxItems", "too_big.array", "maximum", _node.MaxItems, _node.MaxItems);

            // Items can't be removed, so the length is final. Walking a huge array would only add issues to a lost cause.
            settled = true;
            return value;
        }

        if (_contains is not null)
        {
            CheckContains(value, context);
        }

        List<JsonElement>? rewritten = null;
        int index = 0;

        foreach (JsonElement item in value.EnumerateArray())
        {
            ValidationPlan? plan = index < _prefixItems.Length ? _prefixItems[index] : _items;
            JsonElement result = item;

            if (plan is not null)
            {
                int rewrites = context.RewriteCount;
                context.Push(index);

                if (index < _prefixItems.Length)
                {
                    context.Walk("prefixItems", index);
                }
                else
                {
                    context.Walk(_itemsKeyword);
                }

                result = plan.Run(item, context);
                context.Unwalk();
                context.Pop();

                if (context.RewriteCount != rewrites)
                {
                    rewritten ??= [.. value.EnumerateArray().Take(index)];
                }
            }

            rewritten?.Add(result);
            index++;
        }

        JsonElement finished = rewritten is null ? value : JsonStrings.ToArray(rewritten);

        if (_node.UniqueItems == true)
        {
            CheckUnique(finished, context);
        }

        return finished;
    }

    /// <summary>
    /// Equal items share a bucket, so each item is only compared with the earlier items of its bucket. The hash is coarse
    /// on purpose: it must agree with <see cref="JsonEquality"/> (<c>1</c> equals <c>1.0</c>, object order doesn't matter),
    /// so objects and arrays only hash by kind. A long array of those is still quadratic.
    /// </summary>
    private static void CheckUnique(JsonElement value, ValidationContext context)
    {
        JsonElement[] items = new JsonElement[value.GetArrayLength()];
        int[] previous = new int[items.Length];
        Dictionary<int, int> latest = new(items.Length);
        int index = 0;

        foreach (JsonElement item in value.EnumerateArray())
        {
            items[index] = item;
            int bucket = Bucket(item);
            int candidate = latest.GetValueOrDefault(bucket, -1);
            previous[index] = candidate;

            for (; candidate >= 0; candidate = previous[candidate])
            {
                if (JsonEquality.AreEqual(items[candidate], item))
                {
                    context.Report(IssueCodes.InvalidValue, "uniqueItems", "invalid_value.unique", "indexes", new[] { candidate, index }, candidate, index);
                    return;
                }
            }

            latest[bucket] = index;
            index++;
        }
    }

    private static int Bucket(JsonElement item)
    {
        try
        {
            return item.ValueKind switch
            {
                JsonValueKind.String => item.GetString()!.GetHashCode(StringComparison.Ordinal),
                JsonValueKind.Number => (double.Parse(item.GetRawText(), System.Globalization.CultureInfo.InvariantCulture) + 0.0).GetHashCode(),
                _ => (int)item.ValueKind,
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or OverflowException)
        {
            return 0;
        }
    }

    private void CheckContains(JsonElement value, ValidationContext context)
    {
        int matches = 0;

        foreach (JsonElement item in value.EnumerateArray())
        {
            ValidationContext fork = context.Fork();
            _contains!.Run(item, fork);

            if (!fork.HasIssues)
            {
                matches++;
            }
        }

        int minimum = _node.MinContains ?? 1;

        if (matches < minimum)
        {
            context.Report(IssueCodes.TooSmall, "contains", "too_small.contains", "minimum", minimum, minimum);
        }

        if (matches > _node.MaxContains)
        {
            context.Report(IssueCodes.TooBig, "maxContains", "too_big.contains", "maximum", _node.MaxContains, _node.MaxContains);
        }
    }

    private JsonElement RunObject(JsonElement value, ValidationContext context, out bool settled)
    {
        settled = false;

        // Only Strip removes properties, so without it the final count can't be smaller than the raw one.
        if (_node.MaxProperties is { } maximum && _node.Behavior.UnknownProperties != UnknownProperties.Strip && value.GetPropertyCount() > maximum)
        {
            context.Report(IssueCodes.TooBig, "maxProperties", "too_big.object", "maximum", maximum, maximum);
            settled = true;
            return value;
        }

        int count = 0;
        List<string>? unknown = null;
        List<KeyValuePair<string, JsonElement>>? rewritten = null;

        foreach (JsonProperty property in value.EnumerateObject())
        {
            count++;
            string name;

            try
            {
                name = property.Name;
            }
            catch (InvalidOperationException)
            {
                context.Report(IssueCodes.InvalidValue, "propertyNames", "invalid_value.string", "expected", "string");
                return value;
            }

            int rewrites = context.RewriteCount;
            JsonElement original = property.Value;
            JsonElement result = original;
            bool covered = false;

            context.Push(name);

            if (_properties.TryGetValue(name, out ValidationPlan? plan))
            {
                context.Walk("properties", name);
                result = plan.Run(original, context);
                context.Unwalk();
                covered = true;
            }

            foreach ((string patternName, Regex pattern, ValidationPlan patternPlan) in _patternProperties)
            {
                try
                {
                    if (pattern.IsMatch(name))
                    {
                        int before = context.RewriteCount;
                        context.Walk("patternProperties", patternName);
                        JsonElement candidate = patternPlan.Run(original, context);
                        context.Unwalk();
                        result = context.RewriteCount != before ? candidate : result;
                        covered = true;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    context.Report(IssueCodes.InvalidFormat, "patternProperties", "invalid_format.timeout", "pattern", pattern.ToString(), pattern.ToString());
                    covered = true;
                }
            }

            bool keep = true;

            if (!covered)
            {
                if (_namesIgnoringCase?.Contains(name) == true && _node.Behavior.UnknownProperties != UnknownProperties.Strip)
                {
                    (unknown ??= []).Add(name);
                }
                else if (_closedBy is not null)
                {
                    (unknown ??= []).Add(name);
                }
                else if (_additionalProperties is not null)
                {
                    context.Walk(_additionalKeyword);
                    result = _additionalProperties.Run(original, context);
                    context.Unwalk();
                }
                else if (_node.Behavior.UnknownProperties == UnknownProperties.Reject)
                {
                    (unknown ??= []).Add(name);
                }
                else if (_node.Behavior.UnknownProperties == UnknownProperties.Strip)
                {
                    keep = false;
                    context.Rewritten();
                }
            }

            context.Pop();

            if (_propertyNames is not null)
            {
                context.Walk("propertyNames");
                _propertyNames.Run(JsonStrings.ToElement(name), context);
                context.Unwalk();
            }

            if (context.RewriteCount != rewrites)
            {
                rewritten ??= [.. value.EnumerateObject().Take(count - 1).Where(earlier => Kept(earlier.Name)).Select(earlier => KeyValuePair.Create(earlier.Name, earlier.Value))];
            }

            if (keep)
            {
                rewritten?.Add(KeyValuePair.Create(name, result));
            }
        }

        if (unknown is not null)
        {
            context.Report(IssueCodes.UnrecognizedKeys, _closedBy ?? "additionalProperties", IssueCodes.UnrecognizedKeys, "keys", unknown.ToArray(), string.Join(", ", unknown));
        }

        CheckRequired(value, context);

        foreach ((string name, ValidationPlan plan) in _dependentSchemas)
        {
            if (value.TryGetProperty(name, out _))
            {
                context.Walk("dependentSchemas", name);
                plan.Run(value, context);
                context.Unwalk();
            }
        }

        foreach ((string name, SchemaNode node) in _defaults)
        {
            if (!value.TryGetProperty(name, out _))
            {
                rewritten ??= [.. value.EnumerateObject().Where(earlier => Kept(earlier.Name)).Select(earlier => KeyValuePair.Create(earlier.Name, earlier.Value))];
                rewritten.Add(KeyValuePair.Create(name, node.Default!.Value));
                context.Rewritten();
            }
        }

        int finalCount = rewritten?.Count ?? count;

        if (finalCount < _node.MinProperties)
        {
            context.Report(IssueCodes.TooSmall, "minProperties", "too_small.object", "minimum", _node.MinProperties, _node.MinProperties);
        }

        if (finalCount > _node.MaxProperties)
        {
            context.Report(IssueCodes.TooBig, "maxProperties", "too_big.object", "maximum", _node.MaxProperties, _node.MaxProperties);
        }

        return rewritten is null ? value : JsonStrings.ToObject(rewritten);
    }

    private bool Kept(string name) =>
        _node.Behavior.UnknownProperties != UnknownProperties.Strip
        || _additionalProperties is not null
        || _properties.ContainsKey(name)
        || _patternProperties.Any(entry => MatchesWithoutTimeout(entry.Pattern, name));

    private static bool MatchesWithoutTimeout(Regex pattern, string name)
    {
        try
        {
            return pattern.IsMatch(name);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    private void CheckRequired(JsonElement value, ValidationContext context)
    {
        foreach (string name in _node.Required)
        {
            if (!value.TryGetProperty(name, out _))
            {
                context.Push(name);
                context.Report(IssueCodes.Required, "required", IssueCodes.Required, "property", name, name);
                context.Pop();
            }
        }

        foreach ((string name, ImmutableArray<string> dependencies) in _node.DependentRequired)
        {
            if (value.TryGetProperty(name, out _))
            {
                foreach (string dependency in dependencies)
                {
                    if (!value.TryGetProperty(dependency, out _))
                    {
                        context.Push(dependency);
                        context.Report(IssueCodes.Required, "dependentRequired", "required.dependent", "property", dependency, dependency, name);
                        context.Pop();
                    }
                }
            }
        }
    }

    /// <summary>
    /// Every applicator sees the same value, as in JSON Schema, so that a rewrite in one branch can't change
    /// the verdict of another. The result is the last rewrite.
    /// </summary>
    private JsonElement RunApplicators(JsonElement value, ValidationContext context)
    {
        JsonElement result = value;
        int rewrites = context.RewriteCount;

        void Keep(JsonElement candidate)
        {
            if (context.RewriteCount != rewrites)
            {
                result = candidate;
                rewrites = context.RewriteCount;
            }
        }

        if (_node.Behavior.Target is not null)
        {
            ValidationPlan target = Target();
            context.WalkReference(_targetLocation!);
            Keep(target.Run(value, context));
            context.Unwalk();
        }

        for (int index = 0; index < _allOf.Length; index++)
        {
            context.Walk("allOf", index);
            Keep(_allOf[index].Run(value, context));
            context.Unwalk();
        }

        if (!_anyOf.IsEmpty)
        {
            Keep(RunAnyOf(value, context));
        }

        if (!_oneOf.IsEmpty)
        {
            Keep(_discriminated is not null ? RunDiscriminated(value, context) : RunOneOf(value, context));
        }

        if (_not is not null)
        {
            ValidationContext fork = context.Fork();
            _not.Run(value, fork);

            if (!fork.HasIssues || context.LimitExceeded)
            {
                context.Report(IssueCodes.InvalidValue, "not", "invalid_value.not", "expected", "not");
            }
        }

        if (_if is not null)
        {
            ValidationContext fork = context.Fork();
            _if.Run(value, fork);
            ValidationPlan? branch = fork.HasIssues ? _else : _then;

            if (branch is not null)
            {
                context.Walk(fork.HasIssues ? "else" : "then");
                Keep(branch.Run(value, context));
                context.Unwalk();
            }
        }

        return result;
    }

    /// <summary>Built on first use: a long chain of references built up front could exhaust the stack.</summary>
    private ValidationPlan Target()
    {
        if (Volatile.Read(ref _target) is { } plan)
        {
            return plan;
        }

        lock (_plans)
        {
            SchemaNode target = _node.Behavior.Target!();
            _targetLocation = target.Behavior.Location is { } pointer ? ValidationContext.Fragment(pointer)
                : _node.Ref is { } reference && reference.StartsWith('#') ? reference
                : "#";

            ValidationPlan created = Get(target, _plans);
            Volatile.Write(ref _target, created);
            return created;
        }
    }

    private JsonElement RunAnyOf(JsonElement value, ValidationContext context)
    {
        List<ImmutableArray<ParseIssue>> failures = [];

        for (int index = 0; index < _anyOf.Length; index++)
        {
            ValidationContext fork = context.Fork();
            context.Walk("anyOf", index);
            JsonElement result = _anyOf[index].Run(value, fork);
            context.Unwalk();

            if (!fork.HasIssues)
            {
                context.Adopt(fork);
                return result;
            }

            failures.Add(fork.ToIssues());
        }

        context.Report(IssueCodes.InvalidUnion, "anyOf", "invalid_union.none", "errors", failures.ToImmutableArray());
        return value;
    }

    private JsonElement RunOneOf(JsonElement value, ValidationContext context)
    {
        List<ImmutableArray<ParseIssue>> failures = [];
        ValidationContext? matched = null;
        JsonElement result = value;
        int matches = 0;

        for (int index = 0; index < _oneOf.Length; index++)
        {
            ValidationContext fork = context.Fork();
            context.Walk("oneOf", index);
            JsonElement candidate = _oneOf[index].Run(value, fork);
            context.Unwalk();

            if (fork.HasIssues)
            {
                failures.Add(fork.ToIssues());
                continue;
            }

            matches++;
            matched = fork;
            result = candidate;
        }

        if (matches == 1)
        {
            context.Adopt(matched!);
            return result;
        }

        if (matches == 0)
        {
            context.Report(IssueCodes.InvalidUnion, "oneOf", "invalid_union.none", "errors", failures.ToImmutableArray());
        }
        else
        {
            context.Report(IssueCodes.InvalidUnion, "oneOf", "invalid_union.many", "matches", matches, matches);
        }

        return value;
    }

    private JsonElement RunDiscriminated(JsonElement value, ValidationContext context)
    {
        string discriminator = _node.Behavior.Discriminator!;

        if (value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(discriminator, out JsonElement selected)
            && DiscriminatorKeys.Key(selected) is { } key
            && _discriminated!.TryGetValue(key, out (ValidationPlan Plan, int Index) branch))
        {
            context.Walk("oneOf", branch.Index);
            JsonElement result = branch.Plan.Run(value, context);
            context.Unwalk();
            return result;
        }

        string[] options = [.. _discriminated!.Keys.Select(option => option[1..])];
        context.Push(discriminator);
        context.Report(IssueCodes.InvalidUnion, "oneOf", "invalid_union.discriminator", "options", options, string.Join(", ", options));
        context.Pop();
        return value;
    }

    private void RunRefinements(JsonElement value, ValidationContext context)
    {
        if (_node.Behavior.Refinements.IsEmpty || value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        foreach (SchemaRefinement refinement in _node.Behavior.Refinements)
        {
            if (refinement.IsAsync)
            {
                context.Defer(refinement, value);
                continue;
            }

            try
            {
                if (!refinement.Check(value))
                {
                    context.ReportRefinement(refinement);
                }
            }
            catch (JsonException exception)
            {
                context.ReportDeserializationFailed(exception);
            }
        }
    }
}
