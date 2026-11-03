using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zorg.Json.Model;
using Zorg.Json.Validation;

namespace Zorg.Json.Emission;

/// <summary>Writes a <see cref="SchemaNode"/> as JSON Schema, with keywords in a fixed order.</summary>
internal sealed class JsonSchemaEmitter
{
    private const string NumberPattern = @"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?$";
    private const string IntegerPattern = "^-?(0|[1-9][0-9]*)$";
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
    }

    private bool IsInput => _options.Direction == SchemaDirection.Input;

    private bool IsDraft07 => _profile.Dialect == JsonSchemaDialect.Draft07;

    private bool IsOpenApi30 => _profile.Dialect == JsonSchemaDialect.OpenApi30;

    private string ReferencePrefix => _options.ReferencePrefix ?? $"#/{_profile.DefinitionsKeyword}/";

    public static EmitResult Emit(SchemaNode node, JsonSchemaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.Target);

        JsonSchemaEmitter emitter = new(options);
        return emitter.EmitRoot(emitter.Rewrite(node, isRoot: true));
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

    private static JsonNode? ToNode(JsonElement element) => JsonValue.Create(element);

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

        foreach (ISchemaRewriter rewriter in _profile.Rewriters)
        {
            result = Apply(rewriter, result, isRoot);
        }

        return result;
    }

    private SchemaNode Apply(ISchemaRewriter rewriter, SchemaNode node, bool isRoot)
    {
        SchemaNode rewritten = node.MapChildren(child => Apply(rewriter, child, isRoot: false));
        return rewriter.Rewrite(rewritten, new RewriteContext(_options, isRoot, _diagnostics.Add));
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
                JsonObject defs = schema[_profile.DefinitionsKeyword] as JsonObject ?? [];

                foreach ((string name, JsonNode definition) in _definitions.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                {
                    defs[name] = definition;
                }

                schema[_profile.DefinitionsKeyword] = defs;
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

    private static JsonObject BooleanRoot(JsonNode emitted) =>
        emitted.GetValue<bool>() ? [] : new JsonObject { ["not"] = new JsonObject() };

    private JsonNode EmitNode(SchemaNode node, string location)
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
            _definitions[name] = EmitObject(Rewrite(target, isRoot: false), $"/{_profile.DefinitionsKeyword}/{Escape(name)}");
        }

        return ReferencePrefix + Escape(name);
    }

    private JsonObject EmitObject(SchemaNode source, string location)
    {
        List<string> notes = [];
        SchemaNode node = IsInput ? Relocate(source, notes, location) : source;
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

        Add(schema, "$ref", node.Ref?.Replace("#/$defs/", ReferencePrefix, StringComparison.Ordinal));
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
        WriteStrings(schema, node, location);
        WriteArrays(schema, node, notes, location);
        WriteObjects(schema, node, notes, location);
        WriteApplicators(schema, node, notes, location);
        WriteDefinitions(schema, node, location);

        foreach ((string keyword, JsonElement value) in node.Extensions.OrderBy(extension => extension.Key, StringComparer.Ordinal))
        {
            schema[keyword] = ToNode(value);
        }

        foreach (SchemaRefinement refinement in node.Behavior.Refinements)
        {
            notes.Add(refinement.Hint ?? $"Refinement: {refinement.Message}");
            _diagnostics.Add(new EmitDiagnostic(null, $"The refinement \"{refinement.Message}\" can't be expressed in JSON Schema.", location));
        }

        string? description = _options.Unrepresentable == UnrepresentablePolicy.Annotate && notes.Count > 0
            ? string.Join(' ', node.Description is null ? notes : notes.Prepend(node.Description))
            : node.Description;

        if (description is not null)
        {
            schema.Insert(descriptionIndex, "description", description);
        }

        return (IsDraft07 || IsOpenApi30) && schema.ContainsKey("$ref") && schema.Count > 1 ? WrapReference(schema) : schema;
    }

    /// <summary>Before 2019-09, keywords beside <c>$ref</c> are ignored, so <c>$ref</c> moves into an <c>allOf</c>.</summary>
    private static JsonObject WrapReference(JsonObject schema)
    {
        JsonNode reference = schema["$ref"]!.DeepClone();
        schema.Remove("$ref");

        if (schema["allOf"] is JsonArray allOf)
        {
            allOf.Insert(0, new JsonObject { ["$ref"] = reference });
        }
        else
        {
            schema["allOf"] = new JsonArray(new JsonObject { ["$ref"] = reference });
        }

        return schema;
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

        void Note(string keyword, string value)
        {
            relocated.Add($"{keyword} {value}");
            _diagnostics.Add(new EmitDiagnostic(keyword, $"{keyword} applies after {steps}, so the input schema can't state it.", location));
        }

        if (node.Const is { } constant)
        {
            Note("const", constant.GetRawText());
        }

        if (!node.Enum.IsEmpty)
        {
            Note("enum", $"[{string.Join(", ", node.Enum.Select(value => value.GetRawText()))}]");
        }

        if (normalized)
        {
            if (node.MinLength is { } minLength)
            {
                Note("minLength", minLength.ToString(CultureInfo.InvariantCulture));
            }

            if (node.MaxLength is { } maxLength)
            {
                Note("maxLength", maxLength.ToString(CultureInfo.InvariantCulture));
            }

            if (node.Pattern is { } pattern)
            {
                Note("pattern", pattern);
            }

            if (node.Format is { } format)
            {
                Note("format", format);
            }
        }

        if (relocated.Count > 0)
        {
            notes.Add($"After {steps}: {string.Join("; ", relocated)}.");
        }

        SchemaNode result = node with { Const = null, Enum = [] };
        return normalized ? result with { MinLength = null, MaxLength = null, Pattern = null, Format = null } : result;
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
            schema["anyOf"] = new JsonArray([.. alternatives.Select(type => (JsonNode?)new JsonObject { ["type"] = type!.DeepClone() })]);
        }
        else if (written is not null)
        {
            schema["type"] = written;
        }

        if (nullable)
        {
            schema["nullable"] = true;
        }

        if (IsInput && coercion != SchemaCoercion.None)
        {
            bool isInteger = node.Types.HasFlag(JsonTypes.Integer) && !node.Types.HasFlag(JsonTypes.Number);
            string pattern = coercion == SchemaCoercion.Boolean ? BooleanPattern : isInteger ? IntegerPattern : NumberPattern;
            schema["pattern"] = pattern;
        }

        // int32 and friends are OpenAPI's formats. JSON Schema doesn't define them.
        if (_profile.Dialect is JsonSchemaDialect.OpenApi30 or JsonSchemaDialect.OpenApi31
            && node.Behavior.NumberType is { Length: > 0 } format
            && node.Format is null)
        {
            schema["format"] = format;
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

    private void WriteStrings(JsonObject schema, SchemaNode node, string location)
    {
        Add(schema, "minLength", node.MinLength);
        Add(schema, "maxLength", node.MaxLength);
        Add(schema, "pattern", node.Pattern);
        Add(schema, "format", node.Format);
        Add(schema, "contentEncoding", node.ContentEncoding);
        Add(schema, "contentMediaType", node.ContentMediaType);

        if (node.ContentSchema is not null)
        {
            schema["contentSchema"] = EmitNode(node.ContentSchema, $"{location}/contentSchema");
        }
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
            if (IsOpenApi30)
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
            Add(schema, "maxContains", node.MaxContains);
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

        if (node.AdditionalProperties is not null)
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
        WriteList(schema, "oneOf", node.OneOf, location);

        if (node.Not is not null)
        {
            schema["not"] = EmitNode(node.Not, $"{location}/not");
        }

        if (node.If is not null || node.Then is not null || node.Else is not null)
        {
            if (IsOpenApi30)
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
        _diagnostics.Add(new EmitDiagnostic(keyword, $"{_profile.Name} has no {keyword} keyword.", location));
    }
}
