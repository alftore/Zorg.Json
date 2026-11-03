using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private readonly ImmutableArray<(Regex Pattern, ValidationPlan Plan)> _patternProperties;
    private readonly ValidationPlan? _additionalProperties;
    private readonly ValidationPlan? _propertyNames;
    private readonly ImmutableArray<(string Name, ValidationPlan Plan)> _dependentSchemas;
    private readonly ImmutableArray<ValidationPlan> _allOf;
    private readonly ImmutableArray<ValidationPlan> _anyOf;
    private readonly ImmutableArray<ValidationPlan> _oneOf;
    private readonly FrozenDictionary<string, ValidationPlan>? _discriminated;
    private readonly ValidationPlan? _not;
    private readonly ValidationPlan? _if;
    private readonly ValidationPlan? _then;
    private readonly ValidationPlan? _else;
    private readonly Dictionary<SchemaNode, ValidationPlan> _plans;
    private readonly ImmutableArray<(string Name, SchemaNode Node)> _defaults;
    private ValidationPlan? _target;

    private ValidationPlan(SchemaNode node, Dictionary<SchemaNode, ValidationPlan> plans)
    {
        plans.Add(node, this);
        _plans = plans;

        ValidationPlan? Single(SchemaNode? child) => child is null ? null : Get(child, plans);
        ImmutableArray<ValidationPlan> List(ImmutableArray<SchemaNode> children) => [.. children.Select(child => Get(child, plans))];

        _node = node;
        _pattern = node.Pattern is null ? null : PatternCompiler.Compile(node.Pattern);
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
        _contains = Single(node.Contains);
        _properties = node.Properties.Where(property => !property.OutputOnly).ToFrozenDictionary(property => property.Name, property => Get(property.Schema, plans), StringComparer.Ordinal);
        _patternProperties = [.. node.PatternProperties.Select(property => (PatternCompiler.Compile(property.Name), Get(property.Schema, plans)))];
        _additionalProperties = Single(node.AdditionalProperties ?? node.UnevaluatedProperties);
        _propertyNames = Single(node.PropertyNames);
        _dependentSchemas = [.. node.DependentSchemas.Select(property => (property.Name, Get(property.Schema, plans)))];
        _allOf = List(node.AllOf);
        _anyOf = List(node.AnyOf);
        _oneOf = List(node.OneOf);
        _discriminated = node.Behavior.Discriminator is { } discriminator ? Discriminated(node, discriminator, plans) : null;
        _not = Single(node.Not);
        _if = Single(node.If);
        _then = Single(node.Then);
        _else = Single(node.Else);
        _defaults = [.. node.Properties
            .Where(property => property.Schema.Behavior.FillDefault && property.Schema.Default is not null)
            .Select(property => (property.Name, property.Schema))];
    }

    public static ValidationPlan Create(SchemaNode node) => Get(node, new(ReferenceEqualityComparer.Instance));

    public JsonElement Run(JsonElement input, ValidationContext context)
    {
        if (_node.BooleanValue is { } allowed)
        {
            if (!allowed)
            {
                context.Report(IssueCodes.InvalidValue, "false", "invalid_value.false", "expected", null);
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
                    value = RunArray(value, context);
                    break;

                case JsonValueKind.Object:
                    value = RunObject(value, context);
                    break;
            }

            value = RunApplicators(value, context);

            if (context.IssueCount == issues)
            {
                RunRefinements(value, context);
            }
        }

        context.Leave();
        return value;
    }

    private static ValidationPlan Get(SchemaNode node, Dictionary<SchemaNode, ValidationPlan> plans) =>
        plans.TryGetValue(node, out ValidationPlan? plan) ? plan : new ValidationPlan(node, plans);

    private static ExactNumber? Number(JsonElement? value) => value is { } number ? ExactNumber.Parse(number.GetRawText()) : null;

    private static FrozenDictionary<string, ValidationPlan> Discriminated(SchemaNode node, string discriminator, Dictionary<SchemaNode, ValidationPlan> plans)
    {
        Dictionary<string, ValidationPlan> branches = new(StringComparer.Ordinal);

        foreach (SchemaNode branch in node.OneOf)
        {
            SchemaProperty? property = branch.Properties.FirstOrDefault(candidate => candidate.Name == discriminator);

            if (property?.Schema.Const is { ValueKind: JsonValueKind.String } constant)
            {
                branches[constant.GetString()!] = Get(branch, plans);
            }
            else
            {
                foreach (JsonElement value in property?.Schema.Enum ?? [])
                {
                    branches[value.GetString()!] = Get(branch, plans);
                }
            }
        }

        return branches.ToFrozenDictionary(StringComparer.Ordinal);
    }

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

        string? text = value.GetString();

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
        if (_node.Const is { } constant && !JsonElement.DeepEquals(constant, value))
        {
            context.Report(IssueCodes.InvalidValue, "const", "invalid_value.const", "expected", constant.GetRawText(), constant.GetRawText());
        }

        if (!_node.Enum.IsEmpty && !_node.Enum.Any(allowed => JsonElement.DeepEquals(allowed, value)))
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

    private JsonElement RunArray(JsonElement value, ValidationContext context)
    {
        int length = value.GetArrayLength();

        if (length < _node.MinItems)
        {
            context.Report(IssueCodes.TooSmall, "minItems", "too_small.array", "minimum", _node.MinItems, _node.MinItems);
        }

        if (length > _node.MaxItems)
        {
            context.Report(IssueCodes.TooBig, "maxItems", "too_big.array", "maximum", _node.MaxItems, _node.MaxItems);
        }

        if (_node.UniqueItems == true)
        {
            CheckUnique(value, context);
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
                result = plan.Run(item, context);
                context.Pop();

                if (context.RewriteCount != rewrites)
                {
                    rewritten ??= [.. value.EnumerateArray().Take(index)];
                }
            }

            rewritten?.Add(result);
            index++;
        }

        return rewritten is null ? value : JsonStrings.ToArray(rewritten);
    }

    private static void CheckUnique(JsonElement value, ValidationContext context)
    {
        List<JsonElement> seen = [];
        int index = 0;

        foreach (JsonElement item in value.EnumerateArray())
        {
            int first = seen.FindIndex(other => JsonElement.DeepEquals(other, item));

            if (first >= 0)
            {
                context.Report(IssueCodes.InvalidValue, "uniqueItems", "invalid_value.unique", "indexes", new[] { first, index }, first, index);
                return;
            }

            seen.Add(item);
            index++;
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

    private JsonElement RunObject(JsonElement value, ValidationContext context)
    {
        int count = 0;
        List<string>? unknown = null;
        List<KeyValuePair<string, JsonElement>>? rewritten = null;

        foreach (JsonProperty property in value.EnumerateObject())
        {
            count++;
            string name = property.Name;
            int rewrites = context.RewriteCount;
            JsonElement result = property.Value;
            bool covered = false;

            context.Push(name);

            if (_properties.TryGetValue(name, out ValidationPlan? plan))
            {
                result = plan.Run(result, context);
                covered = true;
            }

            foreach ((Regex pattern, ValidationPlan patternPlan) in _patternProperties)
            {
                if (Matches(pattern, name, context))
                {
                    result = patternPlan.Run(result, context);
                    covered = true;
                }
            }

            bool keep = true;

            if (!covered)
            {
                if (_additionalProperties is not null)
                {
                    result = _additionalProperties.Run(result, context);
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
                _propertyNames.Run(JsonStrings.ToElement(name), context);
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
            context.Report(IssueCodes.UnrecognizedKeys, "additionalProperties", IssueCodes.UnrecognizedKeys, "keys", unknown.ToArray(), string.Join(", ", unknown));
        }

        CheckRequired(value, context);

        if (count < _node.MinProperties)
        {
            context.Report(IssueCodes.TooSmall, "minProperties", "too_small.object", "minimum", _node.MinProperties, _node.MinProperties);
        }

        if (count > _node.MaxProperties)
        {
            context.Report(IssueCodes.TooBig, "maxProperties", "too_big.object", "maximum", _node.MaxProperties, _node.MaxProperties);
        }

        foreach ((string name, ValidationPlan plan) in _dependentSchemas)
        {
            if (value.TryGetProperty(name, out _))
            {
                plan.Run(value, context);
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

        return rewritten is null ? value : JsonStrings.ToObject(rewritten);
    }

    private static bool Matches(Regex pattern, string name, ValidationContext context)
    {
        try
        {
            return pattern.IsMatch(name);
        }
        catch (RegexMatchTimeoutException)
        {
            context.Report(IssueCodes.InvalidFormat, "patternProperties", "invalid_format.timeout", "pattern", pattern.ToString(), pattern.ToString());
            return false;
        }
    }

    private bool Kept(string name) =>
        _node.Behavior.UnknownProperties != UnknownProperties.Strip
        || _additionalProperties is not null
        || _properties.ContainsKey(name)
        || _patternProperties.Any(entry => entry.Pattern.IsMatch(name));

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

    private JsonElement RunApplicators(JsonElement input, ValidationContext context)
    {
        JsonElement value = input;

        if (_node.Behavior.Target is not null)
        {
            value = Target().Run(value, context);
        }

        foreach (ValidationPlan plan in _allOf)
        {
            value = plan.Run(value, context);
        }

        if (!_anyOf.IsEmpty)
        {
            value = RunAnyOf(value, context);
        }

        if (!_oneOf.IsEmpty)
        {
            value = _discriminated is not null ? RunDiscriminated(value, context) : RunOneOf(value, context);
        }

        if (_not is not null)
        {
            ValidationContext fork = context.Fork();
            _not.Run(value, fork);

            if (!fork.HasIssues)
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
                value = branch.Run(value, context);
            }
        }

        return value;
    }

    /// <summary>Built on first use: a long chain of references built up front could exhaust the stack.</summary>
    private ValidationPlan Target()
    {
        if (_target is { } plan)
        {
            return plan;
        }

        lock (_plans)
        {
            return _target ??= Get(_node.Behavior.Target!(), _plans);
        }
    }

    private JsonElement RunAnyOf(JsonElement value, ValidationContext context)
    {
        List<ImmutableArray<ParseIssue>> failures = [];

        foreach (ValidationPlan plan in _anyOf)
        {
            ValidationContext fork = context.Fork();
            JsonElement result = plan.Run(value, fork);

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

        foreach (ValidationPlan plan in _oneOf)
        {
            ValidationContext fork = context.Fork();
            JsonElement candidate = plan.Run(value, fork);

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
            && selected.ValueKind == JsonValueKind.String
            && _discriminated!.TryGetValue(selected.GetString()!, out ValidationPlan? plan))
        {
            return plan.Run(value, context);
        }

        string[] options = [.. _discriminated!.Keys];
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
