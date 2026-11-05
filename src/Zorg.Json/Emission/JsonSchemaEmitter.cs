using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text;
using System.Text.Json.Nodes;
using Zorg.Json.Model;
using Zorg.Json.Validation;

namespace Zorg.Json.Emission;

/// <summary>Writes a <see cref="SchemaNode"/> as JSON Schema, with keywords in a fixed order.</summary>
internal sealed class JsonSchemaEmitter
{
    private const string NumberPattern = @"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?$";
    private const string BooleanPattern = "^(true|false)$";

    private readonly JsonSchemaOptions _options;
    private readonly JsonSchemaProfile _profile;
    private readonly ImmutableArray<EmitDiagnostic>.Builder _diagnostics = ImmutableArray.CreateBuilder<EmitDiagnostic>();
    private readonly Dictionary<string, JsonNode> _definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<SchemaNode, string> _definitionNames = new(ReferenceEqualityComparer.Instance);

    private JsonSchemaEmitter(JsonSchemaOptions options)
    {
        _options = options;
        _profile = options.Target;
        _exactness = new SchemaExactness.Checker(_profile.Dialect);
        _rewritten = [.. _profile.Rewriters.Select(_ => new Dictionary<SchemaNode, SchemaNode>(ReferenceEqualityComparer.Instance))];
    }

    /// <summary>
    /// The most subschemas one emission writes. A schema that uses the same schema twice, twenty times over, would write
    /// a million copies of it, so the walk stops long before that.
    /// </summary>
    private const int MaxEmitted = 100_000;

    /// <summary>
    /// How deep the walk may go, counting each <c>$ref</c> it follows. A document with a longer chain of references, or a
    /// schema nested that far, would use up the stack, which ends the process, so it's refused instead.
    /// </summary>
    internal const int MaxDepth = 256;

    private readonly SchemaExactness.Checker _exactness;
    private readonly Dictionary<SchemaNode, SchemaNode>[] _rewritten;
    private List<JsonObject>? _fragments;
    private int _emitted;
    private int _depth;

    private bool IsInput => _options.Direction == SchemaDirection.Input;

    private bool IsDraft07 => _profile.Dialect == JsonSchemaDialect.Draft07;

    private bool IsOpenApi30 => _profile.Dialect == JsonSchemaDialect.OpenApi30;

    private string ReferencePrefix => _options.ReferencePrefix ?? $"#/{_profile.DefinitionsKeyword}/";

    public static EmitResult Emit(SchemaNode node, JsonSchemaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.Target);

        if (options.Unrepresentable == UnrepresentablePolicy.Callback && options.UnrepresentableHandler is null)
        {
            throw new ArgumentException("UnrepresentablePolicy.Callback needs an UnrepresentableHandler.", nameof(options));
        }

        JsonSchemaEmitter emitter = new(options);

        try
        {
            return emitter.EmitRoot(emitter.Rewrite(node, isRoot: true));
        }
        catch (InsufficientExecutionStackException exception)
        {
            throw new InvalidOperationException("The schema is nested too deeply for the stack this thread has left.", exception);
        }
    }

    private static InvalidOperationException TooDeep() => new(
        $"The schema nests more than {MaxDepth} levels deep, counting each $ref it follows. "
        + "Flatten the schema, or break the chain of references.");

    /// <summary>Records a rule that can't be stated and, with the callback policy, collects the handler's fragment for the current subschema.</summary>
    private void Unrepresentable(string? keyword, string message, string location, JsonNode? value = null)
    {
        _diagnostics.Add(new EmitDiagnostic(keyword, message, location));

        if (_options.Unrepresentable == UnrepresentablePolicy.Callback
            && _options.UnrepresentableHandler is { } handler
            && handler(new UnrepresentableContext(keyword, message, location, value)) is { } fragment)
        {
            (_fragments ??= []).Add(fragment);
        }
    }

    private static void Add(JsonObject schema, string keyword, string? value)
    {
        if (value is not null)
        {
            schema[keyword] = value;
        }
    }

    private static void Add(JsonObject schema, string keyword, int? value)
    {
        if (value is not null)
        {
            schema[keyword] = value;
        }
    }

    private static void Add(JsonObject schema, string keyword, bool? value)
    {
        if (value is not null)
        {
            schema[keyword] = value;
        }
    }

    private static void Add(JsonObject schema, string keyword, JsonElement? value)
    {
        if (value is { } element)
        {
            schema[keyword] = ToNode(element);
        }
    }

    private static JsonNode? ToNode(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => JsonObject.Create(element),
        JsonValueKind.Array => JsonArray.Create(element),
        _ => JsonValue.Create(element),
    };

    private static JsonArray ToArray(IEnumerable<JsonElement> values) => new([.. values.Select(ToNode)]);

    private static JsonArray ToArray(IEnumerable<string> values) => new([.. values.Select(value => (JsonNode?)value)]);

    private static string Escape(string name) =>
        name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static JsonNode? Types(JsonTypes types, out bool nullable, bool splitNull)
    {
        nullable = splitNull && types.HasFlag(JsonTypes.Null);
        JsonTypes written = nullable ? types & ~JsonTypes.Null : types;
        string[] names = [.. Enum.GetValues<JsonTypes>()
            .Where(type => type != JsonTypes.None && written.HasFlag(type))
            .OrderBy(type => type == JsonTypes.Null)
            .Select(JsonTypeNames.Name)];

        return names.Length switch
        {
            0 => null,
            1 => names[0],
            _ => ToArray(names),
        };
    }

    private SchemaNode Rewrite(SchemaNode node, bool isRoot)
    {
        SchemaNode result = node;

        for (int index = 0; index < _profile.Rewriters.Length; index++)
        {
            result = Apply(index, result, isRoot, depth: 0);
        }

        return result;
    }

    /// <summary>
    /// A subschema used in many places is rewritten once, because a rewriter's answer for a node doesn't depend on where the
    /// node is. Without that, a schema that uses the same schema twice, again and again, takes exponential time before the
    /// emitter gets to count anything.
    /// </summary>
    private SchemaNode Apply(int index, SchemaNode node, bool isRoot, int depth)
    {
        if (depth > MaxDepth)
        {
            throw TooDeep();
        }

        RuntimeHelpers.EnsureSufficientExecutionStack();

        if (!isRoot && _rewritten[index].TryGetValue(node, out SchemaNode? known))
        {
            return known;
        }

        SchemaNode mapped = node.MapChildren(child => Apply(index, child, isRoot: false, depth + 1));
        SchemaNode result = _profile.Rewriters[index].Rewrite(mapped, new RewriteContext(_options, isRoot, _diagnostics.Add));

        if (!isRoot)
        {
            _rewritten[index][node] = result;
        }

        return result;
    }

    private EmitResult EmitRoot(SchemaNode node)
    {
        JsonNode emitted = EmitNode(node, string.Empty);
        JsonObject schema = emitted as JsonObject ?? BooleanRoot(emitted);

        if (_profile.WritesSchemaKeyword)
        {
            schema.Insert(0, "$schema", _profile.MetaSchema);
        }

        ImmutableDictionary<string, JsonObject> definitions = ImmutableDictionary<string, JsonObject>.Empty;

        if (_definitions.Count > 0)
        {
            if (_options.ReferencePrefix is null)
            {
                JsonObject? existing = schema[_profile.DefinitionsKeyword] as JsonObject;
                JsonObject defs = existing ?? [];

                foreach ((string name, JsonNode definition) in _definitions.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                {
                    defs[name] = definition;
                }

                if (existing is null)
                {
                    // Definitions come before extensions, as they do in WriteDefinitions.
                    schema.Insert(FirstExtension(schema, node), _profile.DefinitionsKeyword, defs);
                }
            }
            else
            {
                definitions = _definitions
                    .Where(entry => entry.Value is JsonObject)
                    .ToImmutableDictionary(entry => entry.Key, entry => (JsonObject)entry.Value, StringComparer.Ordinal);
            }
        }

        foreach (IJsonSchemaPostProcessor processor in _profile.PostProcessors)
        {
            processor.Process(schema, new EmitContext(_options, _diagnostics.Add));
        }

        if (_diagnostics.Count > 0 && _options.Unrepresentable == UnrepresentablePolicy.Throw)
        {
            throw new InvalidOperationException(string.Join(' ', _diagnostics.Select(diagnostic => diagnostic.Message)));
        }

        return new EmitResult(schema, _diagnostics.ToImmutable()) { Definitions = definitions, Target = _profile };
    }

    /// <summary>Where the extensions start in what was written. Not every extension is written, and a reference is written without any.</summary>
    private static int FirstExtension(JsonObject schema, SchemaNode node)
    {
        HashSet<string> names = new(node.Extensions.Select(extension => extension.Key), StringComparer.Ordinal);
        int index = 0;

        foreach ((string keyword, JsonNode? _) in schema)
        {
            if (names.Contains(keyword) || keyword == "x-inert-nullable")
            {
                return index;
            }

            index++;
        }

        return schema.Count;
    }

    private static JsonObject BooleanRoot(JsonNode emitted) =>
        emitted.GetValue<bool>() ? [] : new JsonObject { ["not"] = new JsonObject() };

    private JsonNode EmitNode(SchemaNode node, string location)
    {
        if (++_depth > MaxDepth)
        {
            throw TooDeep();
        }

        RuntimeHelpers.EnsureSufficientExecutionStack();

        try
        {
            return EmitNodeCore(node, location);
        }
        finally
        {
            _depth--;
        }
    }

    private JsonNode EmitNodeCore(SchemaNode node, string location)
    {
        if (node.BooleanValue is { } allowed)
        {
            return IsOpenApi30 ? (allowed ? new JsonObject() : new JsonObject { ["not"] = new JsonObject() }) : JsonValue.Create(allowed);
        }

        string? name = node.Behavior.DefinitionName;

        if (name is not null && node.Behavior.Target is { } target)
        {
            return new JsonObject { ["$ref"] = Reference(target(), name) };
        }

        if (name is not null && location.Length > 0 && _options.Reused == ReusedSchemaHandling.Ref)
        {
            return new JsonObject { ["$ref"] = Reference(node, name) };
        }

        return EmitObject(node, location);
    }

    private string Reference(SchemaNode target, string requested)
    {
        if (!_definitionNames.TryGetValue(target, out string? name))
        {
            name = requested;

            for (int suffix = 2; _definitions.ContainsKey(name); suffix++)
            {
                name = requested + suffix.ToString(CultureInfo.InvariantCulture);
            }

            _definitionNames.Add(target, name);
            _definitions[name] = new JsonObject();

            // A target that's a reference itself must register its own definition. Anything else is written here,
            // because EmitNode would refer a named schema back to this very definition.
            string location = $"/{_profile.DefinitionsKeyword}/{Escape(name)}";
            SchemaNode rewritten = Rewrite(target, isRoot: false);
            _definitions[name] = rewritten.Behavior.Target is null ? EmitObject(rewritten, location) : EmitNode(rewritten, location);
        }

        return ReferencePrefix + FragmentEscape(Escape(name));
    }

    /// <summary>
    /// A <c>$ref</c> is a URI, and what follows the <c>#</c> may not hold a space, a <c>%</c>, a second <c>#</c> or text outside ASCII
    /// as it is. The characters a fragment allows are left as they are, so ordinary names read the same.
    /// </summary>
    private static string FragmentEscape(string pointer)
    {
        StringBuilder text = new(pointer.Length);

        foreach (byte value in Encoding.UTF8.GetBytes(pointer))
        {
            char character = (char)value;

            if (value < 0x80 && (char.IsAsciiLetterOrDigit(character) || "-._~!$&'()*+,;=:@/?".Contains(character, StringComparison.Ordinal)))
            {
                text.Append(character);
            }
            else
            {
                text.Append('%').Append(value.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return text.ToString();
    }

    private JsonObject EmitObject(SchemaNode source, string location)
    {
        List<JsonObject>? outer = _fragments;
        _fragments = null;

        try
        {
            return EmitFields(source, location);
        }
        finally
        {
            _fragments = outer;
        }
    }

    private JsonObject EmitFields(SchemaNode source, string location)
    {
        if (++_emitted > MaxEmitted)
        {
            throw new InvalidOperationException(
                $"The schema writes more than {MaxEmitted} subschemas, usually because a schema is used twice inside itself, again and again. "
                + "Name the shared schema with .Named(\"...\") and set JsonSchemaOptions.Reused = ReusedSchemaHandling.Ref, so that it's written once and referred to.");
        }

        List<string> notes = [];
        SchemaNode written = DropUnwritable(source, notes, location);
        SchemaNode node = IsInput ? RelocateCounts(Relocate(written, notes, location), notes, location) : written;
        JsonObject schema = [];

        Add(schema, IsDraft07 ? "$id" : "$id", node.Id);

        if (IsDraft07 && node.Anchor is not null)
        {
            schema["$id"] = "#" + node.Anchor;
        }
        else
        {
            Add(schema, "$anchor", node.Anchor);
        }

        Add(schema, "$ref", WrittenReference(node.Ref));
        Add(schema, "title", node.Title);
        int descriptionIndex = schema.Count;
        Add(schema, "$comment", node.Comment);
        Add(schema, "default", node.Default);

        if (!node.Examples.IsEmpty)
        {
            if (IsOpenApi30)
            {
                schema["example"] = ToNode(node.Examples[0]);
            }
            else
            {
                schema["examples"] = ToArray(node.Examples);
            }
        }

        Add(schema, "deprecated", node.Deprecated);
        Add(schema, "readOnly", node.ReadOnly);
        Add(schema, "writeOnly", node.WriteOnly);

        WriteTypes(schema, node);
        WriteValues(schema, node);
        WriteNumbers(schema, node);
        WriteStrings(schema, node, notes, location);
        WriteArrays(schema, node, notes, location);
        WriteObjects(schema, node, notes, location);
        int applicatorsIndex = schema.Count;
        WriteApplicators(schema, node, notes, location);
        WriteDefinitions(schema, node, location);

        foreach ((string keyword, JsonElement value) in node.Extensions.OrderBy(extension => extension.Key, StringComparer.Ordinal))
        {
            if (IsOpenApi30 && keyword == "nullable")
            {
                // The document this came from ignores it, but OpenAPI 3.0 would apply it.
                schema["x-inert-nullable"] = ToNode(value);
            }
            else if (IsDraft07 && keyword is "additionalItems" or "dependencies")
            {
                // Inert in the 2020-12 document it came from, but draft-07 would apply it.
                Unrepresentable(keyword, $"{keyword} is an unknown keyword here, but draft-07 would apply it.", location, ToNode(value));
            }
            else
            {
                schema[keyword] = ToNode(value);
            }
        }

        foreach (SchemaRefinement refinement in node.Behavior.Refinements)
        {
            notes.Add(refinement.Hint ?? $"Refinement: {refinement.Message}");
            Unrepresentable(null, $"The refinement \"{refinement.Message}\" can't be expressed in JSON Schema.", location, refinement.Message);
        }

        MergeFragments(schema);

        string? description = _options.Unrepresentable == UnrepresentablePolicy.Annotate && notes.Count > 0
            ? string.Join(' ', node.Description is null ? notes : notes.Prepend(node.Description))
            : node.Description;

        if (description is not null)
        {
            schema.Insert(descriptionIndex, "description", description);
            applicatorsIndex++;
        }

        return (IsDraft07 || IsOpenApi30) && schema.ContainsKey("$ref") && schema.Count > 1 ? WrapReference(schema, applicatorsIndex) : schema;
    }

    /// <summary>Adds what the handler returned. A keyword Zorg already wrote stays as it is.</summary>
    private void MergeFragments(JsonObject schema)
    {
        if (_fragments is null)
        {
            return;
        }

        foreach (JsonObject fragment in _fragments)
        {
            foreach ((string keyword, JsonNode? value) in fragment)
            {
                if (!schema.ContainsKey(keyword))
                {
                    schema[keyword] = value?.DeepClone();
                }
            }
        }
    }

    /// <summary>Before 2019-09, keywords beside <c>$ref</c> are ignored, so <c>$ref</c> moves into an <c>allOf</c>.</summary>
    private static JsonObject WrapReference(JsonObject schema, int applicatorsIndex)
    {
        JsonNode reference = schema["$ref"]!.DeepClone();
        schema.Remove("$ref");

        if (schema["allOf"] is JsonArray allOf)
        {
            allOf.Insert(0, new JsonObject { ["$ref"] = reference });
        }
        else
        {
            schema.Insert(applicatorsIndex - 1, "allOf", new JsonArray(new JsonObject { ["$ref"] = reference }));
        }

        return schema;
    }

    /// <summary>Respells a pointer for draft-07, token by token, because a property may be named like a keyword.</summary>
    private static string ToDraft07Pointer(string reference)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal))
        {
            return reference;
        }

        string[] tokens = reference.Split('/');
        PointerPosition position = PointerPosition.Schema;

        for (int index = 1; index < tokens.Length; index++)
        {
            if (position == PointerPosition.Schema)
            {
                (tokens[index], position) = tokens[index] switch
                {
                    "$defs" or "definitions" => ("definitions", PointerPosition.Name),
                    "dependentSchemas" => ("dependencies", PointerPosition.Name),
                    "prefixItems" => ("items", PointerPosition.Name),
                    "properties" or "patternProperties" or "allOf" or "anyOf" or "oneOf" => (tokens[index], PointerPosition.Name),
                    "items" or "contains" or "additionalProperties" or "propertyNames" or "not" or "if" or "then" or "else" => (tokens[index], PointerPosition.Schema),
                    _ => (tokens[index], PointerPosition.Opaque),
                };
            }
            else if (position == PointerPosition.Name)
            {
                position = PointerPosition.Schema;
            }
        }

        return string.Join('/', tokens);
    }

    private string? WrittenReference(string? reference)
    {
        string? written = reference?.Replace("#/$defs/", ReferencePrefix, StringComparison.Ordinal);
        return IsDraft07 && written is not null ? ToDraft07Pointer(written) : written;
    }

    /// <summary>
    /// Normalization and coercion run before constraints, so a constraint they can affect may
    /// reject input that parsing accepts. The input schema must never be stricter than parsing.
    /// </summary>
    private SchemaNode Relocate(SchemaNode node, List<string> notes, string location)
    {
        bool normalized = !node.Behavior.Normalizers.IsEmpty;
        bool coerced = node.Behavior.Coercion != SchemaCoercion.None;

        if (!normalized && !coerced)
        {
            return node;
        }

        string steps = coerced ? "Coerce" : string.Join(", ", node.Behavior.Normalizers.Select(normalizer => normalizer.Name));
        List<string> relocated = [];

        void Note(string keyword, string value, JsonNode? node)
        {
            relocated.Add($"{keyword} {value}");
            Unrepresentable(keyword, $"{keyword} applies after {steps}, so the input schema can't state it.", location, node);
        }

        if (node.Const is { } constant)
        {
            Note("const", constant.GetRawText(), ToNode(constant));
        }

        if (!node.Enum.IsEmpty)
        {
            Note("enum", $"[{string.Join(", ", node.Enum.Select(value => value.GetRawText()))}]", ToArray(node.Enum));
        }

        if (normalized)
        {
            if (node.MinLength is { } minLength)
            {
                Note("minLength", minLength.ToString(CultureInfo.InvariantCulture), minLength);
            }

            if (node.MaxLength is { } maxLength)
            {
                Note("maxLength", maxLength.ToString(CultureInfo.InvariantCulture), maxLength);
            }

            if (node.Pattern is { } pattern)
            {
                Note("pattern", pattern, pattern);
            }

            if (node.Format is { } format)
            {
                Note("format", format, format);
            }
        }

        if (relocated.Count > 0)
        {
            notes.Add($"After {steps}: {string.Join("; ", relocated)}.");
        }

        SchemaNode result = node with { Const = null, Enum = [] };
        return normalized ? result with { MinLength = null, MaxLength = null, Pattern = null, Format = null } : result;
    }

    /// <summary>
    /// JSON can't spell a string with an unpaired surrogate, which an imported schema may hold. A <c>const</c> or <c>enum</c>
    /// like that is left out and reported, because writing it would fail later, in whoever serializes the schema. Annotations
    /// are left out without a report.
    /// </summary>
    private SchemaNode DropUnwritable(SchemaNode node, List<string> notes, string location)
    {
        SchemaNode result = node;

        if (node.Const is { } constant && SchemaExactness.IsUnwritable(constant))
        {
            notes.Add("Also applies a const that JSON can't spell.");
            Unrepresentable("const", "The const has text with an unpaired surrogate, which JSON can't write.", location);
            result = result with { Const = null };
        }

        if (node.Enum.Any(SchemaExactness.IsUnwritable))
        {
            notes.Add("Also applies an enum that JSON can't spell.");
            Unrepresentable("enum", "The enum has text with an unpaired surrogate, which JSON can't write.", location);
            result = result with { Enum = [] };
        }

        if (node.Default is { } fallback && SchemaExactness.IsUnwritable(fallback))
        {
            result = result with { Default = null };
        }

        if (node.Examples.Any(SchemaExactness.IsUnwritable))
        {
            result = result with { Examples = [.. node.Examples.Where(example => !SchemaExactness.IsUnwritable(example))] };
        }

        if (node.Extensions.Any(extension => SchemaExactness.IsUnwritable(extension.Value)))
        {
            result = result with { Extensions = [.. node.Extensions.Where(extension => !SchemaExactness.IsUnwritable(extension.Value))] };
        }

        return result;
    }

    /// <summary>Property counts apply after Strip and Default, which the input schema can't see.</summary>
    private SchemaNode RelocateCounts(SchemaNode node, List<string> notes, string location)
    {
        bool changesCount = node.Behavior.UnknownProperties == UnknownProperties.Strip
            || node.Properties.Any(property => property.Schema.Behavior.FillDefault);

        if (!changesCount || (node.MinProperties is null && node.MaxProperties is null))
        {
            return node;
        }

        UnsupportedIf(node.MinProperties is not null, "minProperties", notes, location);
        UnsupportedIf(node.MaxProperties is not null, "maxProperties", notes, location);
        return node with { MinProperties = null, MaxProperties = null };
    }

    private void WriteTypes(JsonObject schema, SchemaNode node)
    {
        JsonTypes types = node.Types;
        SchemaCoercion coercion = node.Behavior.Coercion;

        if (IsInput && coercion != SchemaCoercion.None)
        {
            types |= JsonTypes.String;
        }

        JsonNode? written = Types(types, out bool nullable, splitNull: IsOpenApi30);

        if (IsOpenApi30 && written is JsonArray alternatives)
        {
            // nullable only applies beside a type, so it goes on each alternative.
            JsonArray choices = new([.. alternatives.Select(type => (JsonNode?)(nullable
                ? new JsonObject { ["type"] = type!.DeepClone(), ["nullable"] = true }
                : new JsonObject { ["type"] = type!.DeepClone() }))]);

            // A node's own anyOf is another condition, so the two can't share one list.
            if (node.AnyOf.IsEmpty)
            {
                schema["anyOf"] = choices;
            }
            else
            {
                schema["allOf"] = new JsonArray(new JsonObject { ["anyOf"] = choices });
            }

            nullable = false;
        }
        else if (written is not null)
        {
            schema["type"] = written;
        }

        if (nullable)
        {
            schema["nullable"] = true;

            // There is no null type, and nullable alone does nothing without a type beside it. Only null passes.
            if (written is null && node.Const is null && node.Enum.IsEmpty)
            {
                schema["enum"] = new JsonArray((JsonNode?)null);
            }
        }
    }

    private void WriteValues(JsonObject schema, SchemaNode node)
    {
        if (IsOpenApi30 && node.Const is { } constant)
        {
            schema["enum"] = ToArray([constant]);
        }
        else
        {
            Add(schema, "const", node.Const);
        }

        if (!node.Enum.IsEmpty)
        {
            schema["enum"] = ToArray(node.Enum);
        }
    }

    private void WriteNumbers(JsonObject schema, SchemaNode node)
    {
        if (IsOpenApi30)
        {
            Add(schema, "minimum", node.ExclusiveMinimum ?? node.Minimum);
            Add(schema, "maximum", node.ExclusiveMaximum ?? node.Maximum);
            Add(schema, "exclusiveMinimum", node.ExclusiveMinimum is null ? null : true);
            Add(schema, "exclusiveMaximum", node.ExclusiveMaximum is null ? null : true);
        }
        else
        {
            Add(schema, "minimum", node.Minimum);
            Add(schema, "maximum", node.Maximum);
            Add(schema, "exclusiveMinimum", node.ExclusiveMinimum);
            Add(schema, "exclusiveMaximum", node.ExclusiveMaximum);
        }

        Add(schema, "multipleOf", node.MultipleOf);
    }

    private void WriteStrings(JsonObject schema, SchemaNode node, List<string> notes, string location)
    {
        if (node.Behavior.PatternIsUnstated && node.Behavior.PatternRegex is { } regex)
        {
            notes.Add($"Also matches the .NET regular expression {regex}.");
            Unrepresentable("pattern", $"The regular expression {regex} has no ECMA-262 pattern given, so pattern can't be stated.", location, regex.ToString());
        }

        Add(schema, "minLength", node.MinLength);
        Add(schema, "maxLength", node.MaxLength);
        Add(schema, "pattern", CoercionPattern(node) ?? node.Pattern);
        Add(schema, "format", node.Format ?? NumberFormat(node));
        Add(schema, "contentEncoding", node.ContentEncoding);
        Add(schema, "contentMediaType", node.ContentMediaType);

        if (node.ContentSchema is not null)
        {
            schema["contentSchema"] = EmitNode(node.ContentSchema, $"{location}/contentSchema");
        }
    }

    /// <summary>int32 and friends are OpenAPI's formats. JSON Schema doesn't define them.</summary>
    private string? NumberFormat(SchemaNode node) =>
        _profile.Dialect is JsonSchemaDialect.OpenApi30 or JsonSchemaDialect.OpenApi31 && node.Behavior.NumberType is "int32" or "int64" or "float" or "double"
            ? node.Behavior.NumberType
            : null;

    private string? CoercionPattern(SchemaNode node)
    {
        SchemaCoercion coercion = node.Behavior.Coercion;

        if (!IsInput || coercion == SchemaCoercion.None)
        {
            return null;
        }

        return coercion == SchemaCoercion.Boolean ? BooleanPattern : NumberPattern;
    }

    private void WriteArrays(JsonObject schema, SchemaNode node, List<string> notes, string location)
    {
        if (!node.PrefixItems.IsEmpty)
        {
            string keyword = IsDraft07 ? "items" : "prefixItems";

            if (IsOpenApi30)
            {
                Unsupported("prefixItems", notes, location);
            }
            else
            {
                schema[keyword] = new JsonArray([.. node.PrefixItems.Select((item, index) => (JsonNode?)EmitNode(item, $"{location}/{keyword}/{index}"))]);
            }
        }

        // Without prefixItems, items would apply to the tuple's positions too.
        if (node.Items is not null && !(IsOpenApi30 && !node.PrefixItems.IsEmpty))
        {
            string keyword = IsDraft07 && !node.PrefixItems.IsEmpty ? "additionalItems" : "items";
            schema[keyword] = EmitNode(node.Items, $"{location}/{keyword}");
        }

        if (node.Contains is not null)
        {
            // Without minContains, contains would demand a match that minContains: 0 doesn't.
            if (IsOpenApi30 || (IsDraft07 && node.MinContains == 0))
            {
                Unsupported("contains", notes, location);
            }
            else
            {
                schema["contains"] = EmitNode(node.Contains, $"{location}/contains");
            }
        }

        if (IsDraft07 || IsOpenApi30)
        {
            UnsupportedIf(node.MinContains is not null, "minContains", notes, location);
            UnsupportedIf(node.MaxContains is not null, "maxContains", notes, location);
        }
        else
        {
            Add(schema, "minContains", node.MinContains);

            if (node.MaxContains is not null && node.Contains is not null && IsInput && !_exactness.IsExact(node.Contains))
            {
                Unsupported("maxContains", notes, location);
            }
            else
            {
                Add(schema, "maxContains", node.MaxContains);
            }
        }

        Add(schema, "minItems", node.MinItems);
        Add(schema, "maxItems", node.MaxItems);
        Add(schema, "uniqueItems", node.UniqueItems);
    }

    private void WriteObjects(JsonObject schema, SchemaNode node, List<string> notes, string location)
    {
        IEnumerable<SchemaProperty> properties = IsInput ? node.Properties.Where(property => !property.OutputOnly) : node.Properties;

        if (!node.Properties.IsEmpty)
        {
            JsonObject written = [];

            foreach (SchemaProperty property in properties)
            {
                written[property.Name] = EmitNode(property.Schema, $"{location}/properties/{Escape(property.Name)}");
            }

            schema["properties"] = written;
        }

        IEnumerable<string> required = IsInput
            ? node.Required
            : node.Required.Concat(node.Properties.Where(property => property.Schema.Behavior.FillDefault).Select(property => property.Name));

        string[] names = [.. required];

        if (names.Length > 0)
        {
            schema["required"] = ToArray(names);
        }

        if (!node.PatternProperties.IsEmpty)
        {
            if (IsOpenApi30)
            {
                Unsupported("patternProperties", notes, location);
            }
            else
            {
                schema["patternProperties"] = Named(node.PatternProperties, "patternProperties", location);
            }
        }

        // Without patternProperties, additionalProperties would also apply to the properties those cover.
        if (IsOpenApi30 && !node.PatternProperties.IsEmpty)
        {
            Unsupported("additionalProperties", notes, location);
        }
        else if (node.AdditionalProperties is not null)
        {
            schema["additionalProperties"] = EmitNode(node.AdditionalProperties, $"{location}/additionalProperties");
        }
        else if (node.Behavior.UnknownProperties == UnknownProperties.Reject
            || (node.Behavior.UnknownProperties == UnknownProperties.Strip && !IsInput))
        {
            schema["additionalProperties"] = false;
        }

        if (node.PropertyNames is not null)
        {
            if (IsOpenApi30)
            {
                Unsupported("propertyNames", notes, location);
            }
            else
            {
                schema["propertyNames"] = EmitNode(node.PropertyNames, $"{location}/propertyNames");
            }
        }

        WriteDependencies(schema, node, notes, location);
        Add(schema, "minProperties", node.MinProperties);
        Add(schema, "maxProperties", node.MaxProperties);
    }

    private void WriteDependencies(JsonObject schema, SchemaNode node, List<string> notes, string location)
    {
        if (node.DependentRequired.IsEmpty && node.DependentSchemas.IsEmpty)
        {
            return;
        }

        if (IsOpenApi30)
        {
            Unsupported("dependentRequired", notes, location);
            return;
        }

        JsonObject dependentRequired = [];

        foreach ((string name, ImmutableArray<string> dependencies) in node.DependentRequired)
        {
            dependentRequired[name] = ToArray(dependencies);
        }

        JsonObject dependentSchemas = Named(node.DependentSchemas, IsDraft07 ? "dependencies" : "dependentSchemas", location);

        if (IsDraft07)
        {
            foreach ((string name, JsonNode? dependency) in dependentSchemas.ToArray())
            {
                dependentSchemas.Remove(name);
                dependentRequired[name] = dependency;
            }

            schema["dependencies"] = dependentRequired;
            return;
        }

        if (!node.DependentRequired.IsEmpty)
        {
            schema["dependentRequired"] = dependentRequired;
        }

        if (!node.DependentSchemas.IsEmpty)
        {
            schema["dependentSchemas"] = dependentSchemas;
        }
    }

    private void WriteApplicators(JsonObject schema, SchemaNode node, List<string> notes, string location)
    {
        WriteList(schema, "allOf", node.AllOf, location);
        WriteList(schema, "anyOf", node.AnyOf, location);
        WriteOneOf(schema, node, notes, location);

        if (node.Not is not null)
        {
            if (IsInput && !_exactness.IsExact(node.Not))
            {
                Unsupported("not", notes, location);
            }
            else
            {
                schema["not"] = EmitNode(node.Not, $"{location}/not");
            }
        }

        if (node.If is not null || node.Then is not null || node.Else is not null)
        {
            if (IsOpenApi30 || (IsInput && node.If is not null && !_exactness.IsExact(node.If)))
            {
                Unsupported("if", notes, location);
            }
            else
            {
                WriteSingle(schema, "if", node.If, location);
                WriteSingle(schema, "then", node.Then, location);
                WriteSingle(schema, "else", node.Else, location);
            }
        }

        if (IsDraft07 || IsOpenApi30)
        {
            UnsupportedIf(node.UnevaluatedItems is not null, "unevaluatedItems", notes, location);
            UnsupportedIf(node.UnevaluatedProperties is not null, "unevaluatedProperties", notes, location);
        }
        else
        {
            WriteSingle(schema, "unevaluatedItems", node.UnevaluatedItems, location);
            WriteSingle(schema, "unevaluatedProperties", node.UnevaluatedProperties, location);
        }
    }

    /// <summary>
    /// Branches that aren't exact can overlap in the emitted schema though only one matches in Zorg, so
    /// <c>oneOf</c> relaxes to <c>anyOf</c>. Discriminated branches can't overlap, whatever else they leave out.
    /// </summary>
    private void WriteOneOf(JsonObject schema, SchemaNode node, List<string> notes, string location)
    {
        if (!IsInput || node.OneOf.All(branch => _exactness.IsExact(branch)) || DiscriminatorKeys.TryBuild(node, out _))
        {
            WriteList(schema, "oneOf", node.OneOf, location);
        }
        else if (node.AnyOf.IsEmpty)
        {
            Unrepresentable("oneOf", "Some branches can't be stated exactly, so oneOf is written as anyOf.", location);
            WriteList(schema, "anyOf", node.OneOf, location);
        }
        else
        {
            Unsupported("oneOf", notes, location);
        }
    }

    private void WriteDefinitions(JsonObject schema, SchemaNode node, string location)
    {
        if (!node.Defs.IsEmpty)
        {
            schema[_profile.DefinitionsKeyword] = Named(node.Defs, _profile.DefinitionsKeyword, location);
        }
    }

    private void WriteSingle(JsonObject schema, string keyword, SchemaNode? node, string location)
    {
        if (node is not null)
        {
            schema[keyword] = EmitNode(node, $"{location}/{keyword}");
        }
    }

    private void WriteList(JsonObject schema, string keyword, ImmutableArray<SchemaNode> nodes, string location)
    {
        if (nodes.IsEmpty)
        {
            return;
        }

        JsonArray written = schema[keyword] as JsonArray ?? [];

        for (int index = 0; index < nodes.Length; index++)
        {
            written.Add(EmitNode(nodes[index], $"{location}/{keyword}/{index}"));
        }

        schema[keyword] = written;
    }

    private JsonObject Named(ImmutableArray<SchemaProperty> properties, string keyword, string location)
    {
        JsonObject written = [];

        foreach (SchemaProperty property in properties)
        {
            written[property.Name] = EmitNode(property.Schema, $"{location}/{keyword}/{Escape(property.Name)}");
        }

        return written;
    }

    private void UnsupportedIf(bool condition, string keyword, List<string> notes, string location)
    {
        if (condition)
        {
            Unsupported(keyword, notes, location);
        }
    }

    private void Unsupported(string keyword, List<string> notes, string location)
    {
        notes.Add($"Also applies {keyword}, which {_profile.Name} can't state.");
        Unrepresentable(keyword, $"{_profile.Name} has no {keyword} keyword.", location);
    }

    private enum PointerPosition
    {
        Schema,
        Name,
        Opaque,
    }
}
