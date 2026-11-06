using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
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
    private const int NewStackSize = 16 * 1024 * 1024;

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
    private readonly Dictionary<int, List<(JsonElement Value, ValidationPlan Plan, int Index)>>? _discriminated;
    private readonly string[]? _discriminatorOptions;
    private readonly ValidationPlan? _not;
    private readonly ValidationPlan? _if;
    private readonly ValidationPlan? _then;
    private readonly ValidationPlan? _else;
    private readonly SchemaResource? _resource;

    /// <summary>Set when <c>unevaluatedItems</c> depends on what subschemas evaluated, so it runs after them. Otherwise it reads like <c>items</c>.</summary>
    private readonly ValidationPlan? _unevaluatedItems;

    /// <summary>Set when <c>unevaluatedProperties</c> depends on what subschemas evaluated, so it runs after them. Otherwise it reads like <c>additionalProperties</c>.</summary>
    private readonly ValidationPlan? _unevaluatedProperties;
    private readonly Dictionary<SchemaNode, ValidationPlan> _plans;
    private readonly ImmutableArray<(string Name, SchemaNode Node)> _defaults;
    private readonly int _lookups;
    private readonly EnumTable? _enum;
    private ValidationPlan? _target;
    private string? _targetLocation;

    private ValidationPlan(SchemaNode node, Dictionary<SchemaNode, ValidationPlan> plans)
    {
        plans.Add(node, this);
        _plans = plans;

        ValidationPlan? Single(SchemaNode? child) => child is null ? null : Get(child, plans);
        ImmutableArray<ValidationPlan> List(ImmutableArray<SchemaNode> children) => [.. children.Select(child => Get(child, plans))];

        _node = node;
        _resource = node.Behavior.Resource;
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
        bool readsInPlace = node.Behavior.Target is not null || !node.AllOf.IsEmpty || !node.AnyOf.IsEmpty || !node.OneOf.IsEmpty
            || node.If is not null || !node.DependentSchemas.IsEmpty;
        _unevaluatedItems = node.Items is null && (readsInPlace || node.Contains is not null) ? Single(node.UnevaluatedItems) : null;
        _unevaluatedProperties = node.AdditionalProperties is null && readsInPlace ? Single(node.UnevaluatedProperties) : null;
        _items = Single(node.Items ?? (_unevaluatedItems is null ? node.UnevaluatedItems : null));
        _itemsKeyword = node.Items is null ? "unevaluatedItems" : "items";
        _contains = Single(node.Contains);
        _properties = node.Properties.Where(property => !property.OutputOnly).ToFrozenDictionary(property => property.Name, property => Get(property.Schema, plans), StringComparer.Ordinal);
        _namesIgnoringCase = node.Behavior.IgnoresNameCase
            ? node.Properties.Select(property => property.Name).ToFrozenSet(StringComparer.OrdinalIgnoreCase)
            : null;
        _patternProperties = [.. node.PatternProperties.Select(property => (property.Name, PatternCompiler.Compile(property.Name), Get(property.Schema, plans)))];
        _additionalProperties = Single(node.AdditionalProperties ?? (_unevaluatedProperties is null ? node.UnevaluatedProperties : null));
        _additionalKeyword = node.AdditionalProperties is null ? "unevaluatedProperties" : "additionalProperties";
        _closedBy = node.AdditionalProperties?.BooleanValue == false ? "additionalProperties"
            : node.AdditionalProperties is null && _unevaluatedProperties is null && node.UnevaluatedProperties?.BooleanValue == false ? "unevaluatedProperties"
            : null;
        _propertyNames = Single(node.PropertyNames);
        _dependentSchemas = [.. node.DependentSchemas.Select(property => (property.Name, Get(property.Schema, plans)))];
        _allOf = List(node.AllOf);
        _anyOf = List(node.AnyOf);
        _oneOf = List(node.OneOf);
        if (DiscriminatorKeys.TryBuild(node, out List<(JsonElement Value, SchemaNode Branch)> entries))
        {
            _discriminated = [];
            _discriminatorOptions = [.. entries.Select(entry => DiscriminatorKeys.Describe(entry.Value))];

            foreach ((JsonElement entryValue, SchemaNode branch) in entries)
            {
                int hash = JsonEquality.Hash(entryValue);

                if (!_discriminated.TryGetValue(hash, out List<(JsonElement Value, ValidationPlan Plan, int Index)>? bucket))
                {
                    _discriminated[hash] = bucket = [];
                }

                bucket.Add((entryValue, Get(branch, plans), IndexOf(node.OneOf, branch)));
            }
        }

        _not = Single(node.Not);
        _if = Single(node.If);
        _then = Single(node.Then);
        _else = Single(node.Else);
        _defaults = [.. node.Properties
            .Where(property => property.Schema.Behavior.FillDefault && property.Schema.Default is not null)
            .Select(property => (property.Name, property.Schema))];
        _lookups = CountLookups(node, _defaults.Length);
        _enum = EnumTable.Create(node);
    }

    private static int CountLookups(SchemaNode node, int defaults) =>
        node.Required.Length + node.DependentRequired.Sum(entry => 1 + entry.Value.Length) + node.DependentSchemas.Length + defaults;

    /// <summary>An enum with more values than this is looked up by hash, because a failing value would compare with every one.</summary>
    private const int LinearEnum = 8;

    /// <summary>The most values an <c>invalid_value</c> message for an enum lists. The issue's parameters have all of them, shared between issues.</summary>
    private const int MaxListedValues = 50;

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

        bool entered = _resource is not null && context.EnterResource(_resource);
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
                JsonElement original = value;
                value = RunApplicators(value, context);

                if (context.IssueCount == issues && !context.Halted)
                {
                    RunUnevaluated(original, context);
                }

                if (context.IssueCount == issues && !context.Halted)
                {
                    RunRefinements(value, context);
                }
            }
        }

        if (entered)
        {
            context.LeaveResource();
        }

        context.Leave();
        return value;
    }

    private static ValidationPlan Get(SchemaNode node, Dictionary<SchemaNode, ValidationPlan> plans)
    {
        if (plans.TryGetValue(node, out ValidationPlan? plan))
        {
            return plan;
        }

        return RuntimeHelpers.TryEnsureSufficientExecutionStack() ? new ValidationPlan(node, plans) : OnNewStack(node, plans);
    }

    /// <summary>
    /// Plans recurse over the schema, and a schema can be any depth: a chain of <c>$ref</c> needs no nesting. A stack overflow
    /// ends the process, so when the stack runs low the work moves to a thread with a new stack. The caller waits, so only one thread touches <paramref name="plans"/>.
    /// </summary>
    private static ValidationPlan OnNewStack(SchemaNode node, Dictionary<SchemaNode, ValidationPlan> plans)
    {
        ValidationPlan? plan = null;
        ExceptionDispatchInfo? failure = null;

        Thread thread = new(
            () =>
            {
                try
                {
                    plan = new ValidationPlan(node, plans);
                }
#pragma warning disable CA1031 // Rethrown on the caller's thread below.
                catch (Exception exception)
#pragma warning restore CA1031
                {
                    failure = ExceptionDispatchInfo.Capture(exception);
                }
            },
            NewStackSize);

        thread.Start();
        thread.Join();
        failure?.Throw();
        return plan!;
    }

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

        string text = JsonText.GetString(value);

        if (_node.Behavior.Coercion == SchemaCoercion.Boolean)
        {
            if (text is "true" or "false")
            {
                context.Rewritten();
                return JsonElement.Parse(text);
            }

            return value;
        }

        if (ExactNumber.TryParse(text, out _, out _))
        {
            context.Rewritten();
            return JsonElement.Parse(text);
        }

        return value;
    }

    private JsonElement Normalize(JsonElement value, ValidationContext context)
    {
        if (_node.Behavior.Normalizers.IsEmpty || value.ValueKind != JsonValueKind.String)
        {
            return value;
        }

        if (!JsonText.TryGetString(value, out string original))
        {
            context.ReportUndecodable("type");
            return value;
        }

        string text = original;

        foreach (SchemaNormalizer normalizer in _node.Behavior.Normalizers)
        {
            if (normalizer.Admits is { } admits && !admits(text))
            {
                context.ReportTooComplex(normalizer.Name);
                return value;
            }

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
        if (_node.Const is { } constant && !JsonEquality.AreEqual(constant, value, context))
        {
            if (!HoldsUnreadableText(constant.ValueKind, value, context, "const"))
            {
                context.Report(IssueCodes.InvalidValue, "const", "invalid_value.const", "expected", constant.GetRawText(), constant.GetRawText());
            }
        }

        if (_enum is not null && !_enum.Contains(value, context))
        {
            if (!HoldsUnreadableText(_enum.HoldsText ? JsonValueKind.String : JsonValueKind.Null, value, context, "enum"))
            {
                context.Report(IssueCodes.InvalidValue, "enum", "invalid_value.enum", "values", _enum.Texts, _enum.Message);
            }
        }
    }

    /// <summary>
    /// A value that holds a string no schema can read doesn't count as "different from" a constant that has strings, because
    /// that would let <c>not</c> accept it. That ends the parse as a failure, as it does for any other read of the string.
    /// </summary>
    private static bool HoldsUnreadableText(JsonValueKind expected, JsonElement value, ValidationContext context, string keyword)
    {
        if (expected is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null
            || value.ValueKind is not (JsonValueKind.String or JsonValueKind.Array or JsonValueKind.Object))
        {
            return false;
        }

        return HoldsUnreadableText(value, context, keyword);
    }

    /// <summary>True when the value holds an unreadable string, or a limit was hit while looking. The parse has then failed.</summary>
    private static bool HoldsUnreadableText(JsonElement value, ValidationContext context, string keyword)
    {
        ReadOnlySpan<byte> raw = System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(value);

        if (!context.Spend((raw.Length / 16) + 1))
        {
            return true;
        }

        if (!JsonText.HasUndecodableString(raw))
        {
            return false;
        }

        context.ReportUndecodable(keyword);
        return true;
    }

    /// <summary>The values of an <c>enum</c>, prepared once: the texts and the message are shared by every issue.</summary>
    private sealed class EnumTable
    {
        private readonly ImmutableArray<JsonElement> _values;
        private readonly Dictionary<int, List<JsonElement>>? _index;
        private readonly List<JsonElement>? _unhashable;

        public static EnumTable? Create(SchemaNode node) => node.Enum.IsEmpty ? null : new EnumTable(node.Enum);

        private EnumTable(ImmutableArray<JsonElement> values)
        {
            _values = values;
            HoldsNumbers = values.Any(allowed => allowed.ValueKind is JsonValueKind.Number or JsonValueKind.Array or JsonValueKind.Object);
            HoldsText = values.Any(allowed => allowed.ValueKind is JsonValueKind.String or JsonValueKind.Array or JsonValueKind.Object);
            Texts = [.. values.Select(allowed => allowed.GetRawText())];
            Message = string.Join(", ", Texts.Take(MaxListedValues)) + (Texts.Length > MaxListedValues ? $", and {Texts.Length - MaxListedValues} more" : string.Empty);

            if (Texts.Length > LinearEnum)
            {
                _index = [];

                foreach (JsonElement allowed in values)
                {
                    int hash = JsonEquality.Hash(allowed, out bool outOfRange);

                    if (outOfRange)
                    {
                        // Its hash can't find an equal value, so every value is compared with it.
                        (_unhashable ??= []).Add(allowed);
                        continue;
                    }

                    if (!_index.TryGetValue(hash, out List<JsonElement>? bucket))
                    {
                        _index[hash] = bucket = [];
                    }

                    bucket.Add(allowed);
                }
            }
        }

        public string[] Texts { get; }

        /// <summary>True when a value might be equal to one of the strings, or to a container that may hold strings.</summary>
        public bool HoldsText { get; }

        public string Message { get; }

        /// <summary>True when a value might be equal to one of the numbers, or to a container that may hold numbers.</summary>
        private bool HoldsNumbers { get; }

        public bool Contains(JsonElement value, ValidationContext context)
        {
            if (_index is null)
            {
                return Scan(_values, value, context);
            }

            int hash = JsonEquality.Hash(value, out bool outOfRange);

            // A number the hash can't place may equal any allowed number, so it is compared with all of them.
            if (outOfRange)
            {
                // Only a number or a container can be equal to it, and comparing it with every value is charged to the parse.
                return HoldsNumbers && context.Spend((_values.Length / 16) + 1) && Scan(_values, value, context);
            }

            return (_index.TryGetValue(hash, out List<JsonElement>? bucket) && Scan(bucket, value, context))
                || (_unhashable is not null && Scan(_unhashable, value, context));
        }

        private static bool Scan(IEnumerable<JsonElement> candidates, JsonElement value, ValidationContext context)
        {
            foreach (JsonElement allowed in candidates)
            {
                if (context.Halted)
                {
                    return false;
                }

                if (JsonEquality.AreEqual(allowed, value, context))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private void CheckString(JsonElement value, ValidationContext context)
    {
        if (!_readsStrings)
        {
            return;
        }

        if (!JsonText.TryGetString(value, out string text))
        {
            context.ReportUndecodable("type");
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

        if (_format is not null && !(_node.Format == "regex" ? StringFormats.IsRegex(text, context) : _format(text)))
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
            context.ReportTimeout("pattern", _node.Pattern ?? _pattern!.ToString());
        }
    }

    /// <summary>A bound with an exponent too long to hold can't be compared with, so a number against it ends the parse.</summary>
    private bool BoundsOutOfRange() =>
        _minimum?.IsOutOfRange == true || _maximum?.IsOutOfRange == true || _exclusiveMinimum?.IsOutOfRange == true
        || _exclusiveMaximum?.IsOutOfRange == true || _multipleOf?.IsOutOfRange == true;

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

        // Reading the digits takes time in proportion to their number, so a long number is charged for it.
        if (!context.Spend(text.Length / 32))
        {
            return;
        }

        ExactNumber number = ExactNumber.Parse(text);

        if (number.IsOutOfRange || BoundsOutOfRange())
        {
            context.ReportTooComplex("type");
            return;
        }

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

        if (_multipleOf is { } multipleOf && context.Spend(number.MultipleOfCost(multipleOf)) && !number.IsMultipleOf(multipleOf))
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
            if (context.Halted)
            {
                return value;
            }

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
    /// Equal items share a hash (<see cref="JsonEquality.Hash(JsonElement)"/>), so each item is only compared with the earlier items that
    /// hash like it, and those comparisons are charged to the evaluation budget.
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
            int bucket = JsonEquality.Hash(item, out bool outOfRange);

            if (outOfRange && items.Length > 1)
            {
                // The hash can't tell whether this number equals another, and the other numbers are not at hand to compare.
                context.ReportTooComplex("uniqueItems");
                return;
            }
            int candidate = latest.GetValueOrDefault(bucket, -1);
            previous[index] = candidate;

            for (; candidate >= 0; candidate = previous[candidate])
            {
                if (!context.Spend(1))
                {
                    return;
                }

                if (JsonEquality.AreEqual(items[candidate], item, context))
                {
                    context.Report(IssueCodes.InvalidValue, "uniqueItems", "invalid_value.unique", "indexes", new[] { candidate, index }, candidate, index);
                    return;
                }
            }

            latest[bucket] = index;
            index++;
        }
    }

    private void CheckContains(JsonElement value, ValidationContext context)
    {
        int matches = 0;
        int index = 0;

        foreach (JsonElement item in value.EnumerateArray())
        {
            if (context.Halted)
            {
                return;
            }

            ValidationContext fork = context.Fork();
            context.Push(index++);
            _contains!.Run(item, fork);
            context.Pop();

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
            if (context.Halted)
            {
                return value;
            }

            count++;

            if (!JsonText.TryGetName(property, out string name))
            {
                context.ReportUndecodable("propertyNames");
                return value;
            }

            int rewrites = context.RewriteCount;
            JsonElement original = property.Value;
            JsonElement result = original;
            bool covered = false;

            context.Push(name, count);

            if (_properties.TryGetValue(name, out ValidationPlan? plan))
            {
                context.Walk("properties", name);
                result = plan.Run(original, context);
                context.Unwalk();
                covered = true;
            }

            foreach ((string patternName, Regex pattern, ValidationPlan patternPlan) in _patternProperties)
            {
                // Only the match is guarded: an exception from the plan below is a caller's, and passes through.
                bool matched;

                try
                {
                    matched = pattern.IsMatch(name);
                }
                catch (RegexMatchTimeoutException)
                {
                    context.ReportTimeout("patternProperties", pattern.ToString());
                    covered = true;
                    continue;
                }

                if (matched)
                {
                    int before = context.RewriteCount;
                    context.Walk("patternProperties", patternName);
                    JsonElement candidate = patternPlan.Run(original, context);
                    context.Unwalk();
                    result = context.RewriteCount != before ? candidate : result;
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
                rewritten ??= [.. Members(value, context, count - 1)];
            }

            if (keep)
            {
                rewritten?.Add(KeyValuePair.Create(name, result));
            }
        }

        // Each name took work, which a shared subschema repeats. Without this a wide object costs far more than its steps say.
        if (!context.Spend(count))
        {
            return value;
        }

        if (unknown is not null)
        {
            string[] keys = [.. unknown.Take(ValidationContext.MaxListedKeys)];
            context.Report(IssueCodes.UnrecognizedKeys, _closedBy ?? "additionalProperties", IssueCodes.UnrecognizedKeys, "keys", keys, string.Join(", ", keys) + (unknown.Count > keys.Length ? $", and {unknown.Count - keys.Length} more" : string.Empty));
        }

        Lookup? lookup = _lookups > 0 ? new Lookup(value, _lookups > 4) : null;
        CheckRequired(lookup, context);

        foreach ((string name, ValidationPlan plan) in _dependentSchemas)
        {
            if (lookup!.Has(name))
            {
                context.Walk("dependentSchemas", name);
                plan.Run(value, context);
                context.Unwalk();
            }
        }

        foreach ((string name, SchemaNode node) in _defaults)
        {
            if (!lookup!.Has(name))
            {
                rewritten ??= [.. Members(value, context)];
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

    private IEnumerable<KeyValuePair<string, JsonElement>> Members(JsonElement value, ValidationContext context, int limit = int.MaxValue)
    {
        foreach (JsonProperty property in value.EnumerateObject().Take(limit))
        {
            string name = JsonText.GetName(property);

            if (Kept(name, context))
            {
                yield return KeyValuePair.Create(name, property.Value);
            }
        }
    }

    private bool Kept(string name, ValidationContext context) =>
        _node.Behavior.UnknownProperties != UnknownProperties.Strip
        || _additionalProperties is not null
        || _properties.ContainsKey(name)
        || _patternProperties.Any(entry => Matches(entry.Pattern, name, context));

    private static bool Matches(Regex pattern, string name, ValidationContext context)
    {
        try
        {
            return pattern.IsMatch(name);
        }
        catch (RegexMatchTimeoutException)
        {
            context.ReportTimeout("patternProperties", pattern.ToString());
            return true;
        }
    }

    /// <summary>Property lookups on an object. Many of them share one index, because each lookup on the element reads its properties in turn.</summary>
    private sealed class Lookup(JsonElement element, bool indexed)
    {
        private Dictionary<string, JsonElement>? _index;

        public bool Has(string name) =>
            indexed ? (_index ??= JsonText.Index(element)).ContainsKey(name) : JsonText.TryGetProperty(element, name, out _);
    }

    private void CheckRequired(Lookup? lookup, ValidationContext context)
    {
        foreach (string name in _node.Required)
        {
            if (!lookup!.Has(name))
            {
                context.Push(name);
                context.Report(IssueCodes.Required, "required", IssueCodes.Required, "property", name, name);
                context.Pop();
            }
        }

        foreach ((string name, ImmutableArray<string> dependencies) in _node.DependentRequired)
        {
            if (lookup!.Has(name))
            {
                foreach (string dependency in dependencies)
                {
                    if (!lookup.Has(dependency))
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
            ValidationPlan target = Referenced(context);
            context.WalkReference(_targetLocation!);
            Keep(target.Run(value, context));
            context.Unwalk();
        }

        for (int index = 0; index < _allOf.Length && !context.Halted; index++)
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
                context.Walk(fork.HasIssues ? "else" : "then");
                Keep(branch.Run(value, context));
                context.Unwalk();
            }
        }

        return result;
    }

    /// <summary>
    /// Applies <c>unevaluatedProperties</c> and <c>unevaluatedItems</c> to what no subschema evaluated. This only runs on a value
    /// that has passed everything else, because a subschema that failed evaluated nothing, and the verdict is already known.
    /// Finding what was evaluated runs the branches again, which only a schema with these keywords pays for.
    /// </summary>
    private void RunUnevaluated(JsonElement value, ValidationContext context)
    {
        if (_unevaluatedProperties is not null && value.ValueKind == JsonValueKind.Object)
        {
            RunUnevaluatedProperties(value, context);
        }
        else if (_unevaluatedItems is not null && value.ValueKind == JsonValueKind.Array)
        {
            RunUnevaluatedItems(value, context);
        }
    }

    private void RunUnevaluatedProperties(JsonElement value, ValidationContext context)
    {
        HashSet<string> evaluated = new(StringComparer.Ordinal);

        if (!CollectProperties(value, evaluated, context, isOwner: true))
        {
            return;
        }

        List<string>? unknown = null;
        int count = 0;

        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (context.Halted)
            {
                return;
            }

            string name = JsonText.GetName(property);
            count++;

            if (evaluated.Contains(name))
            {
                continue;
            }

            if (_unevaluatedProperties!._node.BooleanValue == false)
            {
                (unknown ??= []).Add(name);
                continue;
            }

            context.Push(name, count);
            context.Walk("unevaluatedProperties");
            _unevaluatedProperties.Run(property.Value, context);
            context.Unwalk();
            context.Pop();
        }

        if (unknown is not null)
        {
            string[] keys = [.. unknown.Take(ValidationContext.MaxListedKeys)];
            context.Report(IssueCodes.UnrecognizedKeys, "unevaluatedProperties", IssueCodes.UnrecognizedKeys, "keys", keys, string.Join(", ", keys) + (unknown.Count > keys.Length ? $", and {unknown.Count - keys.Length} more" : string.Empty));
        }
    }

    private void RunUnevaluatedItems(JsonElement value, ValidationContext context)
    {
        bool[] evaluated = new bool[value.GetArrayLength()];

        if (!CollectItems(value, evaluated, context, isOwner: true))
        {
            return;
        }

        int index = 0;

        foreach (JsonElement item in value.EnumerateArray())
        {
            if (context.Halted)
            {
                return;
            }

            if (!evaluated[index])
            {
                context.Push(index);
                context.Walk("unevaluatedItems");
                _unevaluatedItems!.Run(item, context);
                context.Unwalk();
                context.Pop();
            }

            index++;
        }
    }

    /// <summary>Runs a subschema to see whether it passes, which decides whether what it evaluated counts.</summary>
    private static bool Passes(ValidationPlan plan, JsonElement value, ValidationContext context)
    {
        // Under a dynamic scope the same subschema can mean something else at the same place.
        if (plan._resource is not null)
        {
            ValidationContext scoped = context.Fork();
            plan.Run(value, scoped);
            return !scoped.HasIssues;
        }

        if (context.TryRecall(plan, out bool passed, out string place))
        {
            return passed;
        }

        ValidationContext fork = context.Fork();
        plan.Run(value, fork);
        passed = !fork.HasIssues;
        context.Remember(plan, place, passed);
        return passed;
    }

    /// <summary>
    /// The subschemas that apply to the same value and passed. A subschema of a passed <c>allOf</c> or <c>$ref</c> passed too, so
    /// only the keywords that can fail without failing this schema are run.
    /// </summary>
    private IEnumerable<ValidationPlan> PassedInPlace(JsonElement value, ValidationContext context)
    {
        if (_node.Behavior.Target is not null)
        {
            yield return Referenced(context);
        }

        foreach (ValidationPlan plan in _allOf)
        {
            yield return plan;
        }

        foreach (ValidationPlan plan in _anyOf.Concat(_oneOf))
        {
            if (Passes(plan, value, context))
            {
                yield return plan;
            }
        }

        if (_if is not null)
        {
            bool passed = Passes(_if, value, context);

            if (passed)
            {
                yield return _if;
            }

            if ((passed ? _then : _else) is { } branch)
            {
                yield return branch;
            }
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach ((string name, ValidationPlan plan) in _dependentSchemas)
            {
                if (JsonText.TryGetProperty(value, name, out _))
                {
                    yield return plan;
                }
            }
        }
    }

    /// <summary>
    /// Adds the names this subschema evaluated, which it may only be asked once it has passed. False when a limit stopped the search.
    /// An <c>unevaluatedProperties</c> that passed evaluated every name, unless it's the one asking, which is <paramref name="isOwner"/>.
    /// </summary>
    private bool CollectProperties(JsonElement value, HashSet<string> evaluated, ValidationContext context, bool isOwner = false)
    {
        if (_node.BooleanValue is not null)
        {
            return true;
        }

        if (!context.Enter())
        {
            return false;
        }

        bool finished = context.Spend(value.GetPropertyCount() + 1);

        if (finished)
        {
            foreach (JsonProperty property in value.EnumerateObject())
            {
                string name = JsonText.GetName(property);

                if (_node.AdditionalProperties is not null || (_node.UnevaluatedProperties is not null && !isOwner) || _properties.ContainsKey(name)
                    || _patternProperties.Any(entry => Matches(entry.Pattern, name, context)))
                {
                    evaluated.Add(name);
                }
            }

            foreach (ValidationPlan plan in PassedInPlace(value, context))
            {
                if (context.Halted || !plan.CollectProperties(value, evaluated, context))
                {
                    finished = false;
                    break;
                }
            }
        }

        context.Leave();
        return finished && !context.Halted;
    }

    /// <summary>Marks the items this subschema evaluated, which it may only be asked once it has passed. False when a limit stopped the search.</summary>
    private bool CollectItems(JsonElement value, bool[] evaluated, ValidationContext context, bool isOwner = false)
    {
        if (_node.BooleanValue is not null)
        {
            return true;
        }

        if (!context.Enter())
        {
            return false;
        }

        bool all = _node.Items is not null || (_node.UnevaluatedItems is not null && !isOwner);
        bool finished = context.Spend(evaluated.Length + 1);

        if (finished)
        {
            int index = 0;

            foreach (JsonElement item in value.EnumerateArray())
            {
                context.Push(index);
                evaluated[index] |= all || index < _prefixItems.Length || (_contains is not null && Passes(_contains, item, context));
                context.Pop();
                index++;
            }

            foreach (ValidationPlan plan in PassedInPlace(value, context))
            {
                if (context.Halted || !plan.CollectItems(value, evaluated, context))
                {
                    finished = false;
                    break;
                }
            }
        }

        context.Leave();
        return finished && !context.Halted;
    }

    /// <summary>
    /// What a reference lands on. A <c>$dynamicRef</c> that lands on a <c>$dynamicAnchor</c> of the same name goes on to the outermost
    /// resource in the dynamic scope that has that anchor. Otherwise it behaves like <c>$ref</c>.
    /// </summary>
    private ValidationPlan Referenced(ValidationContext context)
    {
        ValidationPlan target = Target();

        if (_node.DynamicRef is not { } reference)
        {
            return target;
        }

        int fragment = reference.IndexOf('#', StringComparison.Ordinal);

        if (fragment < 0 || target._node.DynamicAnchor != Uri.UnescapeDataString(reference[(fragment + 1)..]))
        {
            return target;
        }

        foreach (SchemaResource resource in context.DynamicScope)
        {
            if (resource.DynamicAnchors.TryGetValue(target._node.DynamicAnchor!, out SchemaNode? node))
            {
                lock (_plans)
                {
                    return Get(node, _plans);
                }
            }
        }

        return target;
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

        for (int index = 0; index < _anyOf.Length && !context.Halted; index++)
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

            failures.Add(context.Retain(fork));
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

        for (int index = 0; index < _oneOf.Length && !context.Halted; index++)
        {
            ValidationContext fork = context.Fork();
            context.Walk("oneOf", index);
            JsonElement candidate = _oneOf[index].Run(value, fork);
            context.Unwalk();

            if (fork.HasIssues)
            {
                failures.Add(context.Retain(fork));
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

    /// <summary>The branch whose discriminator equals the value. A number the hash can't place is compared with them all.</summary>
    private (JsonElement Value, ValidationPlan Plan, int Index)? FindBranch(JsonElement selected, ValidationContext context)
    {
        int hash = JsonEquality.Hash(selected, out bool outOfRange);

        IEnumerable<(JsonElement Value, ValidationPlan Plan, int Index)> candidates = outOfRange
            ? _discriminated!.Values.SelectMany(bucket => bucket)
            : _discriminated!.TryGetValue(hash, out List<(JsonElement Value, ValidationPlan Plan, int Index)>? found) ? found : [];

        foreach ((JsonElement Value, ValidationPlan Plan, int Index) entry in candidates)
        {
            if (context.Halted)
            {
                return null;
            }

            if (JsonEquality.AreEqual(entry.Value, selected, context))
            {
                return entry;
            }
        }

        return null;
    }

    private JsonElement RunDiscriminated(JsonElement value, ValidationContext context)
    {
        string discriminator = _node.Behavior.Discriminator!;

        if (value.ValueKind != JsonValueKind.Object)
        {
            context.Report(IssueCodes.InvalidType, "type", IssueCodes.InvalidType, "expected", "object", "object", TypeName(value.ValueKind));
            return value;
        }

        if (JsonText.TryGetProperty(value, discriminator, out JsonElement selected)
            && FindBranch(selected, context) is { } branch)
        {
            (_, ValidationPlan plan, int index) = branch;
            context.Walk("oneOf", index);
            JsonElement result = plan.Run(value, context);
            context.Unwalk();
            return result;
        }

        // Without a branch, the branches never read the value, so an unreadable string in it can't be passed over.
        if (HoldsUnreadableText(value, context, "oneOf"))
        {
            return value;
        }

        string[] options = _discriminatorOptions!;
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
            catch (MaterializationException exception)
            {
                context.ReportDeserializationFailed(exception);
            }
        }
    }
}
