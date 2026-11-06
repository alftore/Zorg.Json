using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Zorg.Json.Model;
using Zorg.Json.Validation;

namespace Zorg.Json.Import;

/// <summary>
/// Turns a JSON Schema document into a <see cref="SchemaNode"/> tree. References stay shared: a node with a
/// <c>$ref</c> holds a closure over its target, which is filled in once the whole document has been read.
/// </summary>
internal sealed class SchemaImporter
{
    /// <summary>
    /// A keyword the source dialect ignores but 2020-12 honours can't be re-emitted under its own name, because it would
    /// start to apply. It's kept under this prefix, so nothing is lost and verdicts don't change.
    /// </summary>
    private const string InertPrefix = "x-inert-";

    /// <summary>How many in-place evaluations of one value are allowed, per <see cref="JsonSchemaImportOptions.MaxNodes"/>.</summary>
    private const long EvaluationsPerNode = 100;

    private static readonly FrozenSet<string> Only202012 = new[]
    {
        "$defs", "$anchor", "$dynamicRef", "$dynamicAnchor", "prefixItems", "minContains", "maxContains", "dependentRequired",
        "dependentSchemas", "unevaluatedItems", "unevaluatedProperties", "contentSchema",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> SinceDraft07 = new[]
    {
        "$comment", "readOnly", "writeOnly", "if", "then", "else", "contentEncoding", "contentMediaType",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>What Draft-04 lacks on top of <see cref="SinceDraft07"/>. It spells <c>$id</c> as <c>id</c>.</summary>
    private static readonly FrozenSet<string> SinceDraft06 = new[]
    {
        "$id", "$vocabulary", "const", "contains", "deprecated", "examples", "propertyNames",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> UntilDraft07 = new[]
    {
        "definitions", "additionalItems", "dependencies",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>What still means something beside a <c>$ref</c> before 2019-09. The rest is ignored.</summary>
    private static readonly FrozenSet<string> BesideLegacyRef = new[]
    {
        "$ref", "definitions", "title", "description", "$comment", "default", "examples", "readOnly", "writeOnly",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>What the OpenAPI 3.0 schema object honours. <c>definitions</c> is a container for <c>$ref</c>, which Zod writes.</summary>
    private static readonly FrozenSet<string> OpenApi30Keywords = new[]
    {
        "$ref", "definitions", "title", "description", "default", "deprecated", "readOnly", "writeOnly", "nullable", "example", "type", "enum",
        "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf", "minLength", "maxLength", "pattern", "format", "items",
        "minItems", "maxItems", "uniqueItems", "properties", "required", "additionalProperties", "minProperties", "maxProperties", "allOf",
        "anyOf", "oneOf", "not",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Annotations of the OpenAPI 3.0 schema object that nothing validates. They're kept as they are.</summary>
    private static readonly FrozenSet<string> OpenApi30Annotations = new[] { "discriminator", "xml", "externalDocs" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> AnyDialect = new[]
    {
        "$id", "$ref", "$schema", "$vocabulary", "title", "description", "default", "deprecated", "examples", "type", "const", "enum", "minimum",
        "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf", "minLength", "maxLength", "pattern", "format", "items",
        "contains", "minItems", "maxItems", "uniqueItems", "properties", "required", "patternProperties", "additionalProperties",
        "propertyNames", "minProperties", "maxProperties", "allOf", "anyOf", "oneOf", "not",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The base of a document without an <c>$id</c>, which only lets relative references resolve against each other.</summary>
    internal const string DocumentBase = DocumentOrigin + "zorg-document-without-an-id";

    /// <summary>Where the invented base lives. A real document doesn't refer here, and a resource under it has no base of its own.</summary>
    internal const string DocumentOrigin = "https://zorg.invalid/";

    private readonly JsonElement _root;
    private readonly JsonSchemaImportOptions _options;
    private readonly JsonSchemaImportDialect _dialect;
    private readonly Dictionary<string, SchemaNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<SchemaNode, string> _locations = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(string Resource, string Name), string> _anchors = [];

    /// <summary>Each base URI in the document, with the pointer of its resource. Nothing outside the document is read.</summary>
    private readonly Dictionary<string, string> _resources = new(StringComparer.Ordinal) { [DocumentBase] = string.Empty };

    /// <summary>The base URI of each resource, by its pointer.</summary>
    private readonly Dictionary<string, string> _bases = new(StringComparer.Ordinal) { [string.Empty] = DocumentBase };
    private readonly List<PendingReference> _references = [];
    private readonly List<(string Resource, string Name, string Pointer)> _dynamicAnchors = [];
    private readonly Dictionary<string, SchemaResource> _dynamicResources = new(StringComparer.Ordinal);
    private readonly bool _usesDynamicScope;
    private readonly HashSet<string> _patterns = new(StringComparer.Ordinal);
    private readonly HashSet<string> _walked = new(StringComparer.Ordinal);
    private readonly bool _isDialectAssumed;
    private int _count;
    private long _patternLength;

    private SchemaImporter(JsonElement root, JsonSchemaImportOptions options, JsonSchemaImportDialect dialect, bool isDialectAssumed, List<(string Uri, string Pointer)> documents)
    {
        foreach ((string uri, string pointer) in documents)
        {
            // The URI a document was given names it, whatever $id it has.
            if (!_resources.TryAdd(uri, pointer))
            {
                throw Error(JsonSchemaImportCodes.InvalidSchema, string.Empty, $"The document URI \"{uri}\" is declared twice.");
            }

            _bases[pointer] = uri;
        }

        _root = root;
        _usesDynamicScope = dialect == JsonSchemaImportDialect.Draft202012 && root.GetRawText().Contains("$dynamic", StringComparison.Ordinal);
        _options = options;
        _dialect = dialect;
        _isDialectAssumed = isDialectAssumed;
    }

    private enum Position
    {
        Schema,
        Map,
        List,
        Opaque,
    }

    private bool IsLegacy => _dialect != JsonSchemaImportDialect.Draft202012;

    private bool IsDraft04 => _dialect == JsonSchemaImportDialect.Draft04;

    private bool IsOpenApi30 => _dialect == JsonSchemaImportDialect.OpenApi30;

    /// <summary>Draft-04 and OpenAPI 3.0 share the boolean exclusive limits, the integers without a fraction, and the lack of boolean schemas.</summary>
    private bool IsDraft04Style => IsDraft04 || IsOpenApi30;

    public static SchemaNode Import(JsonElement schema, JsonSchemaImportOptions? options)
    {
        if (schema.ValueKind == JsonValueKind.Undefined)
        {
            throw Error(JsonSchemaImportCodes.InvalidSchema, string.Empty, "A schema must be an object or a boolean.");
        }

        JsonSchemaImportOptions effective = options ?? new JsonSchemaImportOptions();
        JsonElement root = schema.Clone();

        JsonSchemaImportDialect dialect = DetectDialect(root, effective);
        List<(string Uri, string Pointer)> documents = [];

        if (effective.Documents is { Count: > 0 })
        {
            root = Bundle(root, effective, dialect, documents);
        }

        bool isAssumed = effective.DefaultDialect == JsonSchemaImportDialect.Draft202012 && !(root.ValueKind == JsonValueKind.Object && root.TryGetProperty("$schema", out _));
        return new SchemaImporter(root, effective, dialect, isAssumed, documents).Run();
    }

    /// <summary>
    /// Writes the other documents into the definitions of the schema, as embedded resources. The text of what's there is kept as it is,
    /// so numbers and escapes don't change.
    /// </summary>
    private static JsonElement Bundle(JsonElement root, JsonSchemaImportOptions options, JsonSchemaImportDialect dialect, List<(string Uri, string Pointer)> documents)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Error(JsonSchemaImportCodes.InvalidSchema, string.Empty, "A schema with Documents must be an object.");
        }

        string container = dialect == JsonSchemaImportDialect.Draft202012 ? "$defs" : "definitions";
        string idKeyword = dialect == JsonSchemaImportDialect.Draft04 ? "id" : "$id";
        List<string> members = [];
        List<string> definitions = [];
        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!property.NameEquals(container))
            {
                members.Add(property.ToString());
            }
            else if (property.Value.ValueKind != JsonValueKind.Object)
            {
                throw Error(JsonSchemaImportCodes.InvalidSchema, $"/{container}", $"{container} must be an object.");
            }
            else
            {
                foreach (JsonProperty definition in property.Value.EnumerateObject())
                {
                    definitions.Add(definition.ToString());

                    if (JsonText.TryGetName(definition, out string name))
                    {
                        names.Add(name);
                    }
                }
            }
        }

        foreach ((string uri, string text) in InOrder(options.Documents!))
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out _) || SplitUri(DocumentBase, uri) is not { Fragment: "#" } target)
            {
                throw Error(JsonSchemaImportCodes.InvalidSchema, string.Empty, $"The document URI \"{uri}\" must be absolute, with no fragment.");
            }

            JsonElement document = Parse(text, options);
            int number = 1;

            while (!names.Add($"document-{number}"))
            {
                number++;
            }

            string name = $"document-{number}";
            documents.Add((target.Uri, $"/{container}/{name}"));
            definitions.Add($"{JsonSerializedText(name)}:{WithBase(document, idKeyword, target.Uri)}");
        }

        members.Add($"{JsonSerializedText(container)}:{{{string.Join(',', definitions)}}}");
        return Parse($"{{{string.Join(',', members)}}}", options);

        static IEnumerable<KeyValuePair<string, string>> InOrder(IReadOnlyDictionary<string, string> all) =>
            all.OrderBy(entry => entry.Key, StringComparer.Ordinal);
    }

    private static string JsonSerializedText(string text) => $"\"{JsonEncodedText.Encode(text).ToString()}\"";

    /// <summary>A document with no id of its own has the URI it was given as its base.</summary>
    private static string WithBase(JsonElement document, string idKeyword, string uri)
    {
        if (document.ValueKind != JsonValueKind.Object || document.TryGetProperty(idKeyword, out _))
        {
            return document.GetRawText();
        }

        List<string> members = [$"{JsonSerializedText(idKeyword)}:{JsonSerializedText(uri)}"];
        members.AddRange(document.EnumerateObject().Select(property => property.ToString()));
        return $"{{{string.Join(',', members)}}}";
    }

    private SchemaResource ResourceOf(string pointer)
    {
        if (!_dynamicResources.TryGetValue(pointer, out SchemaResource? resource))
        {
            _dynamicResources[pointer] = resource = new SchemaResource();
        }

        return resource;
    }

    private SchemaNode Run()
    {
        SchemaNode node = Build(_root, new Scope(string.Empty, string.Empty, DocumentBase, 0, IsWalked: true));
        ResolveReferences();

        foreach ((string resource, string name, string pointer) in _dynamicAnchors)
        {
            ResourceOf(resource).DynamicAnchors.TryAdd(name, _nodes[pointer]);
        }

        CheckInPlaceGraph();
        return node;
    }

    public static SchemaNode Import(string schema, JsonSchemaImportOptions? options)
    {
        JsonSchemaImportOptions effective = options ?? new JsonSchemaImportOptions();
        return Import(Parse(schema, effective), effective);
    }

    private static JsonElement Parse(string schema, JsonSchemaImportOptions options)
    {
        // A subschema takes at most two levels of JSON, as in properties/name.
        int depth = (options.MaxDepth * 2) + 2;

        try
        {
            return JsonElement.Parse(schema, new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = depth });
        }
        catch (JsonException exception)
        {
            // Too deep is the only failure that a reader with no depth limit survives and one with the limit doesn't.
            string code = Reads(schema, int.MaxValue) && !Reads(schema, depth) ? JsonSchemaImportCodes.LimitExceeded : JsonSchemaImportCodes.InvalidSchema;
            throw new JsonSchemaImportException(code, string.Empty, exception.Message, exception);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // A lone surrogate in the .NET string has no UTF-8 spelling, and one in an escaped property name can't be compared.
            throw new JsonSchemaImportException(JsonSchemaImportCodes.InvalidSchema, string.Empty, "The text isn't valid Unicode.", exception);
        }
    }

    /// <summary>Reads tokens only, without building a document, so that a schema nested 100,000 levels deep is refused quickly.</summary>
    private static bool Reads(string schema, int maxDepth)
    {
        try
        {
            Utf8JsonReader reader = new(System.Text.Encoding.UTF8.GetBytes(schema), new JsonReaderOptions { MaxDepth = maxDepth });

            while (reader.Read())
            {
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonSchemaImportDialect DetectDialect(JsonElement root, JsonSchemaImportOptions options)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("$schema", out JsonElement declared))
        {
            return options.DefaultDialect;
        }

        if (declared.ValueKind != JsonValueKind.String)
        {
            throw Error(JsonSchemaImportCodes.InvalidSchema, "/$schema", "$schema must be a string.");
        }

        string dialect = Text(declared, "/$schema");

        return ToDialect(dialect)
            ?? throw Error(JsonSchemaImportCodes.UnsupportedDialect, "/$schema", $"The dialect \"{dialect}\" can't be imported. Use Draft 2020-12, draft-07, draft-06 or draft-04.");
    }

    private static JsonSchemaImportDialect? ToDialect(string uri) => uri.TrimEnd('#') switch
    {
        "https://json-schema.org/draft/2020-12/schema" => JsonSchemaImportDialect.Draft202012,
        "http://json-schema.org/draft-07/schema" or "https://json-schema.org/draft-07/schema" => JsonSchemaImportDialect.Draft07,
        "http://json-schema.org/draft-06/schema" or "https://json-schema.org/draft-06/schema" => JsonSchemaImportDialect.Draft06,
        "http://json-schema.org/draft-04/schema" or "https://json-schema.org/draft-04/schema" => JsonSchemaImportDialect.Draft04,
        _ => null,
    };

    private static JsonSchemaImportException Error(string code, string location, string message) => new(code, location, message);

    /// <summary>
    /// The text of a JSON string. The parser accepts an escaped lone surrogate, which has no decoding, and Zorg refuses
    /// such text in instances too. Values Zorg only keeps, such as <c>const</c>, are never decoded.
    /// </summary>
    private static string Text(JsonElement value, string location)
    {
        try
        {
            return value.GetString()!;
        }
        catch (InvalidOperationException exception)
        {
            throw new JsonSchemaImportException(JsonSchemaImportCodes.InvalidSchema, location, "The text has an unpaired surrogate, so it isn't valid Unicode.", exception);
        }
    }

    private static string Name(JsonProperty property, string location)
    {
        try
        {
            return property.Name;
        }
        catch (InvalidOperationException exception)
        {
            throw new JsonSchemaImportException(JsonSchemaImportCodes.InvalidSchema, location, "A name has an unpaired surrogate, so it isn't valid Unicode.", exception);
        }
    }

    private static string Escape(string name) =>
        name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static string Unescape(string token) =>
        token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

    private static string DecodeToken(string token) => Unescape(Uri.UnescapeDataString(token));

    private static bool IsAnchorName(string name) =>
        name.Length > 0
        && (char.IsAsciiLetter(name[0]) || name[0] == '_')
        && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    /// <summary>Draft-04 calls an integer a number without a fraction or exponent part, so <c>1.0</c> isn't one.</summary>
    private static bool IsDraft04Integer(string number) => number.AsSpan().IndexOfAny('.', 'e', 'E') < 0;

    private bool IsKeyword(string keyword) =>
        AnyDialect.Contains(keyword) || SinceDraft07.Contains(keyword) || UntilDraft07.Contains(keyword)
        || (IsOpenApi30 && (OpenApi30Keywords.Contains(keyword) || OpenApi30Annotations.Contains(keyword)));

    private static JsonTypes ToType(JsonElement value, string location) => value.ValueKind == JsonValueKind.String
        ? Text(value, location) switch
        {
            "null" => JsonTypes.Null,
            "boolean" => JsonTypes.Boolean,
            "integer" => JsonTypes.Integer,
            "number" => JsonTypes.Number,
            "string" => JsonTypes.String,
            "array" => JsonTypes.Array,
            "object" => JsonTypes.Object,
            _ => throw Error(JsonSchemaImportCodes.InvalidSchema, location, $"\"{Text(value, location)}\" isn't a JSON Schema type."),
        }
        : throw Error(JsonSchemaImportCodes.InvalidSchema, location, "A type must be a string.");

    private static ImmutableArray<string> ToUniqueStrings(JsonElement value, string location)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw Error(JsonSchemaImportCodes.InvalidSchema, location, "Expected an array of strings.");
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        ImmutableArray<string>.Builder names = ImmutableArray.CreateBuilder<string>(value.GetArrayLength());

        foreach (JsonElement item in value.EnumerateArray())
        {
            string name = item.ValueKind == JsonValueKind.String ? Text(item, $"{location}/{names.Count}") : string.Empty;

            if (item.ValueKind != JsonValueKind.String || !seen.Add(name))
            {
                throw Error(JsonSchemaImportCodes.InvalidSchema, location, "Expected an array of unique strings.");
            }

            names.Add(name);
        }

        return names.MoveToImmutable();
    }

    private static void RejectUnsupported(Dictionary<string, JsonElement> values, Scope at)
    {
        if (values.ContainsKey("$dynamicRef") && values.ContainsKey("$ref"))
        {
            throw Error(JsonSchemaImportCodes.UnsupportedKeyword, at.Keyword("$dynamicRef"), "$dynamicRef beside $ref isn't supported. Put one of them in an allOf.");
        }
    }

    private static (string Token, Position Next) Translate(string token, string name, JsonElement? schema)
    {
        bool isArrayForm = schema is { ValueKind: JsonValueKind.Object } parent
            && parent.TryGetProperty("items", out JsonElement items)
            && items.ValueKind == JsonValueKind.Array;

        return name switch
        {
            "definitions" => ("$defs", Position.Map),
            "dependencies" => ("dependentSchemas", Position.Map),
            "items" => isArrayForm ? ("prefixItems", Position.List) : (token, Position.Schema),
            "additionalItems" => isArrayForm ? ("items", Position.Schema) : (token, Position.Opaque),
            "properties" or "patternProperties" => (token, Position.Map),
            "allOf" or "anyOf" or "oneOf" => (token, Position.List),
            "not" or "if" or "then" or "else" or "additionalProperties" or "propertyNames" or "contains" => (token, Position.Schema),
            _ => (token, Position.Opaque),
        };
    }

    private static JsonElement? Step(JsonElement element, string token)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            return element.TryGetProperty(token, out JsonElement child) ? child : null;
        }

        bool isCanonical = token == "0" || (token.Length > 0 && token[0] != '0' && token.All(char.IsAsciiDigit));

        if (element.ValueKind == JsonValueKind.Array
            && isCanonical
            && int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
            && index < element.GetArrayLength())
        {
            return element[index];
        }

        return null;
    }

    private static IEnumerable<SchemaNode> InPlaceEdges(SchemaNode node)
    {
        if (node.Behavior.Target is { } target)
        {
            yield return target();
        }

        foreach (SchemaNode child in node.AllOf.Concat(node.AnyOf).Concat(node.OneOf).Concat(node.DependentSchemas.Select(property => property.Schema)))
        {
            yield return child;
        }

        SchemaNode?[] singles = [node.Not, node.If, node.Then, node.Else];

        foreach (SchemaNode child in singles.OfType<SchemaNode>())
        {
            yield return child;
        }
    }

    private SchemaNode Build(JsonElement schema, Scope at, bool allowsBoolean = false)
    {
        if (at.Depth > _options.MaxDepth)
        {
            throw Error(JsonSchemaImportCodes.LimitExceeded, at.Pointer, $"Subschemas nest more than {_options.MaxDepth} levels deep.");
        }

        if (++_count > _options.MaxNodes)
        {
            throw Error(JsonSchemaImportCodes.LimitExceeded, at.Pointer, $"The document has more than {_options.MaxNodes} subschemas.");
        }

        bool isBooleanLegal = allowsBoolean || !IsDraft04Style;

        SchemaNode node = schema.ValueKind switch
        {
            JsonValueKind.True when isBooleanLegal => SchemaNode.True,
            JsonValueKind.False when isBooleanLegal => SchemaNode.False,
            JsonValueKind.Object => BuildObject(schema, at),
            _ => throw Error(JsonSchemaImportCodes.InvalidSchema, at.Pointer, isBooleanLegal ? "A schema must be an object or a boolean." : "A schema must be an object in draft-04."),
        };

        // A boolean too: a reference to it has to find where it was written.
        node = node with { Behavior = node.Behavior with { Location = at.Pointer } };
        _nodes.TryAdd(at.Pointer, node);
        _locations.TryAdd(node, at.Pointer);

        if (at.IsWalked)
        {
            _walked.Add(at.Pointer);
        }

        return node;
    }

    private SchemaNode BuildObject(JsonElement schema, Scope at)
    {
        Dictionary<string, JsonElement> values = new(StringComparer.Ordinal);

        foreach (JsonProperty property in schema.EnumerateObject())
        {
            string keyword = Name(property, at.Pointer);

            if (!values.TryAdd(keyword, property.Value))
            {
                throw Error(JsonSchemaImportCodes.InvalidSchema, at.Keyword(keyword), "The keyword appears twice.");
            }
        }

        if (!IsLegacy)
        {
            RejectUnsupported(values, at);
        }

        if (_isDialectAssumed && values.TryGetValue("nullable", out JsonElement nullable) && nullable.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            throw Error(JsonSchemaImportCodes.UnsupportedDialect, at.Keyword("nullable"), "nullable is a keyword of OpenAPI 3.0, and this document has no $schema, so it would be ignored. Set DefaultDialect = JsonSchemaImportDialect.OpenApi30 to read it as OpenAPI 3.0.");
        }

        Keywords keywords = new(this, values, at, ignoresSiblings: IsLegacy && values.ContainsKey("$ref"));
        (string? id, string? anchor, Scope scope) = ReadIdentity(keywords, at);
        string? reference = keywords.String("$ref");
        SchemaBehavior behavior = SchemaBehavior.Empty;

        if (reference is not null)
        {
            ReferenceTarget target = new();
            _references.Add(new PendingReference(target, reference, scope));
            behavior = behavior with { Target = target.Get };
            reference = NormalizeReference(reference, scope);
        }

        string? dynamicRef = IsLegacy ? null : keywords.String("$dynamicRef");
        string? dynamicAnchor = IsLegacy ? null : keywords.String("$dynamicAnchor");

        if (dynamicRef is not null)
        {
            ReferenceTarget target = new();
            _references.Add(new PendingReference(target, dynamicRef, scope));
            behavior = behavior with { Target = target.Get };
        }

        if (dynamicAnchor is not null)
        {
            if (!IsAnchorName(dynamicAnchor))
            {
                throw Error(JsonSchemaImportCodes.InvalidSchema, at.Keyword("$dynamicAnchor"), $"\"{dynamicAnchor}\" isn't a valid anchor name.");
            }

            if (at.IsWalked && !_anchors.TryAdd((scope.Resource, dynamicAnchor), at.Pointer))
            {
                throw Error(JsonSchemaImportCodes.InvalidSchema, at.Pointer, $"The anchor \"{dynamicAnchor}\" is declared twice.");
            }

            _dynamicAnchors.Add((scope.Resource, dynamicAnchor, at.Pointer));
        }

        if (_usesDynamicScope)
        {
            behavior = behavior with { Resource = ResourceOf(scope.Resource) };
        }

        string? format = keywords.String("format");

        if (format is not null)
        {
            CheckFormat(format, at);
            bool annotates = _options.Formats == FormatHandling.AnnotateOnly || (_options.Formats == FormatHandling.ByDialect && _dialect == JsonSchemaImportDialect.Draft202012);
            behavior = annotates ? behavior with { FormatIsAnnotation = true } : behavior;
        }

        ImmutableArray<SchemaProperty> defs = keywords.Named(IsLegacy ? "definitions" : "$defs", scope);
        (ImmutableArray<SchemaNode> prefixItems, SchemaNode? items) = ReadItems(keywords, scope);
        (ImmutableArray<KeyValuePair<string, ImmutableArray<string>>> dependentRequired, ImmutableArray<SchemaProperty> dependentSchemas) = ReadDependencies(keywords, scope);
        bool rejectsEverything = keywords.Peek("enum", out JsonElement declared) && declared.ValueKind == JsonValueKind.Array && declared.GetArrayLength() == 0;
        ImmutableArray<SchemaNode> allOf = keywords.NonEmptyList("allOf", scope);
        (JsonElement? minimum, JsonElement? exclusiveMinimum) = ReadLimit(keywords, "minimum", "exclusiveMinimum", at);
        (JsonElement? maximum, JsonElement? exclusiveMaximum) = ReadLimit(keywords, "maximum", "exclusiveMaximum", at);
        JsonTypes types = ReadTypes(keywords, strict: IsOpenApi30);

        if (IsDraft04Style && types.HasFlag(JsonTypes.Integer) && !types.HasFlag(JsonTypes.Number))
        {
            behavior = behavior with { NumberCheck = IsDraft04Integer, NumberType = "integer" };
        }

        // Only beside a type, and only the type: an enum or const still has to list null.
        if (IsOpenApi30 && keywords.Boolean("nullable") == true && types != JsonTypes.None)
        {
            types |= JsonTypes.Null;
        }

        SchemaNode node = new()
        {
            Id = id,
            Anchor = anchor,
            Ref = reference,
            DynamicRef = dynamicRef,
            DynamicAnchor = dynamicAnchor,
            Defs = defs,
            Title = keywords.String("title"),
            Description = keywords.String("description"),
            Comment = keywords.String("$comment"),
            Default = keywords.Element("default"),
            Examples = IsOpenApi30 ? (keywords.Element("example") is { } example ? [example] : []) : keywords.Elements("examples"),
            Deprecated = keywords.Boolean("deprecated"),
            ReadOnly = keywords.Boolean("readOnly"),
            WriteOnly = keywords.Boolean("writeOnly"),
            Types = types,
            Const = keywords.Element("const"),
            Enum = keywords.Elements("enum"),
            Minimum = minimum,
            Maximum = maximum,
            ExclusiveMinimum = exclusiveMinimum,
            ExclusiveMaximum = exclusiveMaximum,
            MultipleOf = keywords.PositiveNumber("multipleOf"),
            MinLength = keywords.Count("minLength"),
            MaxLength = keywords.Count("maxLength"),
            Pattern = keywords.Pattern("pattern"),
            Format = format,
            ContentEncoding = keywords.String("contentEncoding"),
            ContentMediaType = keywords.String("contentMediaType"),
            ContentSchema = keywords.Schema("contentSchema", scope),
            PrefixItems = prefixItems,
            Items = items,
            Contains = keywords.Schema("contains", scope),
            MinContains = keywords.Count("minContains"),
            MaxContains = keywords.Count("maxContains"),
            MinItems = keywords.Count("minItems"),
            MaxItems = keywords.Count("maxItems"),
            UniqueItems = keywords.Boolean("uniqueItems"),
            Properties = keywords.Named("properties", scope),
            Required = keywords.Strings("required"),
            PatternProperties = keywords.PatternNamed("patternProperties", scope),
            AdditionalProperties = keywords.Schema("additionalProperties", scope),
            PropertyNames = keywords.Schema("propertyNames", scope),
            DependentRequired = dependentRequired,
            DependentSchemas = dependentSchemas,
            MinProperties = keywords.Count("minProperties"),
            MaxProperties = keywords.Count("maxProperties"),

            // The model reads an empty enum as absent, but the keyword rejects everything.
            AllOf = rejectsEverything ? allOf.Add(SchemaNode.False) : allOf,
            AnyOf = keywords.NonEmptyList("anyOf", scope),
            OneOf = keywords.NonEmptyList("oneOf", scope),
            Not = keywords.Schema("not", scope),
            If = keywords.Schema("if", scope),
            Then = keywords.Schema("then", scope),
            Else = keywords.Schema("else", scope),
            UnevaluatedItems = keywords.Schema("unevaluatedItems", scope),
            UnevaluatedProperties = keywords.Schema("unevaluatedProperties", scope),
            Behavior = behavior,
        };

        return node with { Extensions = ReadExtensions(values, at, keywords.IgnoresSiblings) };
    }

    private (string? Id, string? Anchor, Scope Scope) ReadIdentity(Keywords keywords, Scope at)
    {
        string keyword = IsDraft04 ? "id" : "$id";
        string? id = keywords.String(keyword);
        string? anchor = keywords.String("$anchor");
        Scope scope = at;

        if (anchor is not null && !IsAnchorName(anchor))
        {
            throw Error(JsonSchemaImportCodes.InvalidSchema, at.Keyword("$anchor"), $"\"{anchor}\" isn't a valid anchor name.");
        }

        if (id is not null)
        {
            int fragment = id.IndexOf('#', StringComparison.Ordinal);
            bool hasFragment = fragment >= 0 && fragment < id.Length - 1;

            if (hasFragment && !IsLegacy)
            {
                throw Error(JsonSchemaImportCodes.InvalidSchema, at.Keyword("$id"), "$id can't have a non-empty fragment. Use $anchor.");
            }

            // Before 2019-09 a fragment names the schema, and the rest is its base. 2020-12 spells the two separately.
            // A fragment on the URI of the resource around it names a schema in that resource, and starts no new one.
            bool sameResource = fragment > 0 && SplitUri(at.Base, id[..fragment]) is { } named && named.Uri == at.Base;

            if (hasFragment && id[fragment + 1] == '/')
            {
                // Generators write the place of a schema as its id, as in #/definitions/a. It names what a pointer already names.
                id = fragment == 0 ? null : id[..fragment];
                hasFragment = false;
                fragment = id is null ? 0 : -1;
            }
            else if (hasFragment)
            {
                anchor = id[(fragment + 1)..];

                if (!IsAnchorName(anchor))
                {
                    throw Error(JsonSchemaImportCodes.InvalidSchema, at.Keyword(keyword), $"\"{id}\" has a fragment that isn't a plain name.");
                }
            }

            if (fragment != 0 && at.Pointer.Length > 0 && !sameResource)
            {
                scope = at with { Resource = at.Pointer };
            }

            id = sameResource || (fragment == 0 && hasFragment) ? null : hasFragment ? id![..fragment] : id;

            if (id is not null && SplitUri(at.Base, id) is { } own)
            {
                scope = scope with { Base = own.Uri };

                // 11: two resources can't share a URI, and a target outside the walked tree can only add to what's known.
                if (!_resources.TryAdd(own.Uri, scope.Resource) && _resources[own.Uri] != scope.Resource && at.IsWalked)
                {
                    throw Error(JsonSchemaImportCodes.InvalidSchema, at.Keyword(keyword), $"The $id \"{id}\" is declared twice.");
                }

                _bases[scope.Resource] = own.Uri;
            }
        }

        // Only the walked tree has anchors to refer to, and a second one of the same name would clash when read again.
        if (!at.IsWalked)
        {
            anchor = null;
        }
        else if (anchor is not null && !_anchors.TryAdd((scope.Resource, anchor), at.Pointer))
        {
            throw Error(JsonSchemaImportCodes.InvalidSchema, at.Pointer, $"The anchor \"{anchor}\" is declared twice.");
        }

        return (id, anchor, scope);
    }

    private void CheckFormat(string format, Scope at)
    {
        if (_options.UnknownFormats == UnknownFormatHandling.Error && !StringFormats.IsKnown(format))
        {
            throw Error(JsonSchemaImportCodes.UnsupportedKeyword, at.Keyword("format"), $"The format \"{format}\" is unknown.");
        }
    }

    private static JsonTypes ReadTypes(Keywords keywords, bool strict)
    {
        if (!keywords.Take("type", out JsonElement value, out string location))
        {
            return JsonTypes.None;
        }

        if (strict && (value.ValueKind == JsonValueKind.Array || (value.ValueKind == JsonValueKind.String && Text(value, location) == "null")))
        {
            throw Error(JsonSchemaImportCodes.InvalidSchema, location, "OpenAPI 3.0 takes one type name, and has no null type. Use nullable: true.");
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return ToType(value, location);
        }

        JsonTypes types = JsonTypes.None;

        foreach (JsonElement item in value.EnumerateArray())
        {
            JsonTypes type = ToType(item, location);

            if (types.HasFlag(type))
            {
                throw Error(JsonSchemaImportCodes.InvalidSchema, location, "A type appears twice.");
            }

            types |= type;
        }

        return types != JsonTypes.None ? types : throw Error(JsonSchemaImportCodes.InvalidSchema, location, "type can't be an empty array.");
    }

    /// <summary>In draft-04 the exclusive keyword is a boolean that changes what the inclusive one means.</summary>
    private (JsonElement? Inclusive, JsonElement? Exclusive) ReadLimit(Keywords keywords, string inclusive, string exclusive, Scope at)
    {
        JsonElement? limit = keywords.Number(inclusive);

        if (!IsDraft04Style)
        {
            return (limit, keywords.Number(exclusive));
        }

        bool? isExclusive = keywords.Boolean(exclusive);

        if (isExclusive is not null && limit is null)
        {
            throw Error(JsonSchemaImportCodes.InvalidSchema, at.Keyword(exclusive), $"{exclusive} needs {inclusive} beside it in {(IsOpenApi30 ? "OpenAPI 3.0" : "draft-04")}.");
        }

        return isExclusive == true ? (null, limit) : (limit, null);
    }

    private (ImmutableArray<SchemaNode> PrefixItems, SchemaNode? Items) ReadItems(Keywords keywords, Scope scope)
    {
        if (!IsLegacy)
        {
            return (keywords.NonEmptyList("prefixItems", scope), keywords.Schema("items", scope));
        }

        if (!keywords.Peek("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
        {
            return ([], keywords.Schema("items", scope));
        }

        if (IsOpenApi30)
        {
            throw Error(JsonSchemaImportCodes.InvalidSchema, scope.Keyword("items"), "OpenAPI 3.0 has no tuples: items is one schema.");
        }

        return (keywords.NonEmptyList("items", scope), keywords.Schema("additionalItems", scope));
    }

    private (ImmutableArray<KeyValuePair<string, ImmutableArray<string>>> Required, ImmutableArray<SchemaProperty> Schemas) ReadDependencies(Keywords keywords, Scope scope)
    {
        if (!IsLegacy)
        {
            return (keywords.DependentRequired(), keywords.Named("dependentSchemas", scope));
        }

        if (!keywords.Take("dependencies", out JsonElement dependencies, out string location))
        {
            return ([], []);
        }

        if (dependencies.ValueKind != JsonValueKind.Object)
        {
            throw Error(JsonSchemaImportCodes.InvalidSchema, location, "dependencies must be an object.");
        }

        ImmutableArray<KeyValuePair<string, ImmutableArray<string>>>.Builder required = ImmutableArray.CreateBuilder<KeyValuePair<string, ImmutableArray<string>>>();
        ImmutableArray<SchemaProperty>.Builder schemas = ImmutableArray.CreateBuilder<SchemaProperty>();

        foreach (JsonProperty property in dependencies.EnumerateObject())
        {
            string name = Name(property, location);
            Scope child = scope.Child("dependencies", name);

            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                required.Add(KeyValuePair.Create(name, ToUniqueStrings(property.Value, child.Pointer)));
            }
            else
            {
                schemas.Add(new SchemaProperty(name, Build(property.Value, child)));
            }
        }

        return (required.ToImmutable(), schemas.ToImmutable());
    }

    private ImmutableArray<KeyValuePair<string, JsonElement>> ReadExtensions(Dictionary<string, JsonElement> remaining, Scope at, bool ignoresSiblings)
    {
        ImmutableArray<KeyValuePair<string, JsonElement>>.Builder extensions = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonElement>>(remaining.Count);

        foreach ((string keyword, JsonElement value) in remaining.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (keyword == "$schema")
            {
                if (at.Pointer.Length == 0)
                {
                    continue;
                }

                CheckNestedDialect(value, at);
            }

            bool isIgnoredId = IsDraft04 && keyword == "id";
            bool isUnknown = !isIgnoredId && !IsKeyword(keyword) && !Only202012.Contains(keyword) && !keyword.StartsWith("x-", StringComparison.Ordinal);

            if (isUnknown && _options.UnknownKeywords == UnknownKeywordHandling.Error)
            {
                throw Error(JsonSchemaImportCodes.UnsupportedKeyword, at.Keyword(keyword), $"The keyword {keyword} is unknown.");
            }

            extensions.Add(KeyValuePair.Create(IsInert(keyword, ignoresSiblings) ? InertPrefix + keyword : keyword, value));
        }

        return extensions.ToImmutable();
    }

    private void CheckNestedDialect(JsonElement value, Scope at)
    {
        if (value.ValueKind != JsonValueKind.String || ToDialect(Text(value, at.Keyword("$schema"))) != _dialect)
        {
            throw Error(JsonSchemaImportCodes.UnsupportedDialect, at.Keyword("$schema"), "A subschema can't change the dialect.");
        }
    }

    private bool IsInert(string keyword, bool ignoresSiblings)
    {
        if (!IsLegacy)
        {
            return false;
        }

        if (IsOpenApi30)
        {
            // These all mean something in 2020-12, or would be taken for a Draft 2020-12 keyword when read again.
            return keyword is "$schema" or "nullable" or "example" or "patternProperties"
                || Only202012.Contains(keyword)
                || SinceDraft07.Contains(keyword)
                || SinceDraft06.Contains(keyword)
                || (ignoresSiblings && IsKeyword(keyword));
        }

        // $defs is among the 2020-12 keywords, so it stays inert here. A $ref into it gets a home of its own.
        return keyword == "$schema"
            || Only202012.Contains(keyword)
            || (_dialect is JsonSchemaImportDialect.Draft06 or JsonSchemaImportDialect.Draft04 && SinceDraft07.Contains(keyword))
            || (IsDraft04 && SinceDraft06.Contains(keyword))
            || (ignoresSiblings && IsKeyword(keyword));
    }

    private bool IsActive(string keyword, bool ignoresSiblings)
    {
        if (ignoresSiblings && !BesideLegacyRef.Contains(keyword))
        {
            return false;
        }

        return _dialect switch
        {
            JsonSchemaImportDialect.Draft202012 => !UntilDraft07.Contains(keyword),
            JsonSchemaImportDialect.Draft07 => !Only202012.Contains(keyword),
            JsonSchemaImportDialect.Draft06 => !Only202012.Contains(keyword) && !SinceDraft07.Contains(keyword),
            JsonSchemaImportDialect.OpenApi30 => OpenApi30Keywords.Contains(keyword),
            _ => !Only202012.Contains(keyword) && !SinceDraft07.Contains(keyword) && !SinceDraft06.Contains(keyword),
        };
    }

    private void CheckPattern(string pattern, string location)
    {
        if (pattern.Length > _options.MaxPatternLength)
        {
            throw Error(JsonSchemaImportCodes.LimitExceeded, location, $"The pattern is longer than {_options.MaxPatternLength} characters.");
        }

        // Each distinct text is compiled once and shared with the validation plan, so only new text costs anything.
        if (_patterns.Add(pattern) && (_patternLength += pattern.Length) > _options.MaxTotalPatternLength)
        {
            throw Error(JsonSchemaImportCodes.LimitExceeded, location, $"The patterns add up to more than MaxTotalPatternLength, which is {_options.MaxTotalPatternLength}.");
        }

        try
        {
            PatternCompiler.Compile(pattern);
        }
        catch (ArgumentException exception)
        {
            throw new JsonSchemaImportException(JsonSchemaImportCodes.InvalidPattern, location, exception.Message, exception);
        }
    }

    /// <summary>
    /// Before 2019-09 a pointer can spell keywords the model doesn't keep, such as <c>definitions</c>. The node's
    /// <c>$ref</c> reads as the model does, so each token is translated by its position. The emitter doesn't read it: it follows the target.
    /// </summary>
    private string NormalizeReference(string reference, Scope scope)
    {
        if (!IsLegacy || !reference.StartsWith("#/", StringComparison.Ordinal))
        {
            return reference;
        }

        JsonElement? current = Walk(scope.Resource);
        Position position = Position.Schema;
        List<string> written = ["#"];

        foreach (string token in reference[2..].Split('/'))
        {
            string name = DecodeToken(token);
            (string spelled, Position next) = position switch
            {
                Position.Schema => Translate(token, name, current),
                Position.Map or Position.List => (token, Position.Schema),
                _ => (token, Position.Opaque),
            };

            written.Add(spelled);
            position = next;
            current = current is { } element ? Step(element, name) : null;
        }

        return string.Join('/', written);
    }

    /// <summary>The pointer from the document root that a <c>$ref</c> names, or null for an anchor.</summary>
    private static string? PointerOf(string reference, string resource)
    {
        string fragment = Uri.UnescapeDataString(reference[1..]);
        return fragment.Length == 0 || fragment[0] == '/' ? resource + fragment : null;
    }

    private JsonElement? Walk(string pointer)
    {
        JsonElement? current = _root;

        foreach (string token in pointer.Split('/').Skip(1))
        {
            current = current is { } element ? Step(element, Unescape(token)) : null;
        }

        return current;
    }

    private void ResolveReferences()
    {
        // The list grows when a target outside the walked tree has references of its own, and such a target can add an $id that
        // an earlier reference names, so a reference that names no known resource waits until nothing else can add one.
        List<PendingReference> waiting = [];

        for (int index = 0; index < _references.Count; index++)
        {
            PendingReference reference = _references[index];

            if (NamesKnownResource(reference))
            {
                reference.Target.Node = Find(reference);
            }
            else
            {
                waiting.Add(reference);
            }

            if (index == _references.Count - 1 && waiting.Find(NamesKnownResource) is { } ready)
            {
                waiting.Remove(ready);
                _references.Add(ready);
            }
        }

        foreach (PendingReference reference in waiting)
        {
            reference.Target.Node = Find(reference);
        }
    }

    private bool NamesKnownResource(PendingReference reference) =>
        reference.Text.StartsWith('#') || (SplitUri(reference.At.Base, reference.Text) is { } resolved && _resources.ContainsKey(resolved.Uri));

    /// <summary>A URI resolved against a base, split into the URI of a resource and its fragment with the <c>#</c>. Null when it can't be read.</summary>
    internal static (string Uri, string Fragment)? SplitUri(string baseUri, string reference)
    {
        Uri? resolved;

        // TryCreate throws for some bases, such as a scheme of its own with a path.
        try
        {
            if (!Uri.TryCreate(baseUri, UriKind.Absolute, out Uri? parent) || !Uri.TryCreate(parent, reference, out resolved))
            {
                return null;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException)
        {
            return null;
        }

        string text = resolved.AbsoluteUri;
        int fragment = text.IndexOf('#', StringComparison.Ordinal);
        return fragment < 0 ? (text, "#") : (text[..fragment], text[fragment..]);
    }

    private SchemaNode Find(PendingReference reference)
    {
        string location = reference.At.Keyword("$ref");
        string resource = reference.At.Resource;
        string text = reference.Text;

        if (!text.StartsWith('#'))
        {
            if (text.StartsWith(DocumentOrigin, StringComparison.OrdinalIgnoreCase)
                || SplitUri(reference.At.Base, text) is not { } resolved || !_resources.TryGetValue(resolved.Uri, out string? named))
            {
                throw Error(JsonSchemaImportCodes.UnresolvedRef, location, $"\"{text}\" isn't in this document. A reference can reach #, a JSON Pointer, an $anchor and any $id in the document, and nothing is fetched.");
            }

            resource = named;
            text = resolved.Fragment;
        }

        string? pointer = PointerOf(text, resource);

        if (pointer is null)
        {
            string name = Uri.UnescapeDataString(text[1..]);

            pointer = _anchors.GetValueOrDefault((resource, name))
                ?? throw Error(JsonSchemaImportCodes.UnresolvedRef, location, $"No $anchor is named \"{name}\".");
        }

        if (_nodes.TryGetValue(pointer, out SchemaNode? node))
        {
            return node;
        }

        if (Walk(pointer) is { ValueKind: JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False } target)
        {
            return Build(target, new Scope(pointer, resource, _bases.GetValueOrDefault(resource, DocumentBase), 0, IsWalked: false));
        }

        string hint = IsOpenApi30 && reference.Text.StartsWith("#/components/", StringComparison.Ordinal)
            ? " An OpenAPI reference points into the whole OpenAPI document, so this document has to hold the components it names."
            : string.Empty;

        throw Error(JsonSchemaImportCodes.UnresolvedRef, location, $"\"{reference.Text}\" doesn't point at a schema.{hint}");
    }

    /// <summary>
    /// Walks the edges that stay on the same instance. A cycle among them never ends, a long chain would exhaust the
    /// stack at validation time, and shared targets can multiply into exponential work per value, as
    /// in <c>a: allOf[b, b], b: allOf[c, c]</c>. Iterative, because the chain length is what's being measured.
    /// </summary>
    private void CheckInPlaceGraph()
    {
        long maxEvaluations = _options.MaxNodes * EvaluationsPerNode;
        Dictionary<SchemaNode, (int Depth, long Evaluations)> finished = new(ReferenceEqualityComparer.Instance);
        HashSet<SchemaNode> active = new(ReferenceEqualityComparer.Instance);
        Stack<Frame> stack = new();

        foreach (SchemaNode start in _locations.Keys)
        {
            if (start.BooleanValue is not null || finished.ContainsKey(start))
            {
                continue;
            }

            active.Add(start);
            stack.Push(new Frame(start));

            while (stack.TryPeek(out Frame? frame))
            {
                if (frame.Next() is { } child)
                {
                    if (active.Contains(child))
                    {
                        SchemaNode reference = stack.FirstOrDefault(entry => entry.Node.Ref is not null)?.Node ?? frame.Node;
                        throw Error(JsonSchemaImportCodes.RefCycle, Location(reference), "This cycle of references never reaches a child of the instance, so evaluating it wouldn't end.");
                    }

                    if (finished.TryGetValue(child, out (int Depth, long Evaluations) known))
                    {
                        frame.Add(known.Depth, known.Evaluations);
                    }
                    else if (child.BooleanValue is null)
                    {
                        active.Add(child);
                        stack.Push(new Frame(child));
                    }

                    continue;
                }

                stack.Pop();
                active.Remove(frame.Node);
                int length = frame.Depth + 1;
                long evaluations = frame.Evaluations + 1;

                if (frame.Depth > _options.MaxDepth)
                {
                    throw Error(JsonSchemaImportCodes.LimitExceeded, Location(frame.Node), $"A chain of references is longer than {_options.MaxDepth}.");
                }

                if (evaluations > maxEvaluations)
                {
                    throw Error(JsonSchemaImportCodes.LimitExceeded, Location(frame.Node), $"Shared references make one value cost more than {maxEvaluations} evaluations.");
                }

                finished[frame.Node] = (length, evaluations);

                if (stack.TryPeek(out Frame? parent))
                {
                    parent.Add(length, evaluations);
                }
            }
        }
    }

    private string Location(SchemaNode node)
    {
        string pointer = _locations[node];
        return node.Ref is null ? pointer : $"{pointer}/$ref";
    }

    private sealed class ReferenceTarget
    {
        public SchemaNode? Node { get; set; }

        public SchemaNode Get() => Node!;
    }

    private sealed record PendingReference(ReferenceTarget Target, string Text, Scope At);

    private sealed class Frame(SchemaNode node)
    {
        private readonly SchemaNode[] _edges = [.. InPlaceEdges(node)];
        private int _index;

        public SchemaNode Node { get; } = node;

        public int Depth { get; private set; }

        public long Evaluations { get; private set; }

        public SchemaNode? Next() => _index < _edges.Length ? _edges[_index++] : null;

        public void Add(int depth, long evaluations)
        {
            Depth = Math.Max(Depth, depth);
            Evaluations += evaluations;
        }
    }

    /// <summary>Where a subschema is: its pointer, the root of its <c>$id</c> resource, its base URI, and its depth.</summary>
    private readonly record struct Scope(string Pointer, string Resource, string Base, int Depth, bool IsWalked)
    {
        public string Keyword(string keyword) => $"{Pointer}/{Escape(keyword)}";

        public Scope Child(string keyword) => this with { Pointer = Keyword(keyword), Depth = Depth + 1 };

        public Scope Child(string keyword, string name) => this with { Pointer = $"{Keyword(keyword)}/{Escape(name)}", Depth = Depth + 1 };

        public Scope Child(string keyword, int index) => Child(keyword, index.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Reads keywords off one schema object. What's left afterwards goes to <see cref="SchemaNode.Extensions"/>.</summary>
    private sealed class Keywords(SchemaImporter importer, Dictionary<string, JsonElement> values, Scope at, bool ignoresSiblings)
    {
        public bool IgnoresSiblings => ignoresSiblings;

        public bool Peek(string keyword, out JsonElement value) => values.TryGetValue(keyword, out value) && importer.IsActive(keyword, ignoresSiblings);

        public bool Take(string keyword, out JsonElement value, out string location)
        {
            location = string.Empty;

            if (!importer.IsActive(keyword, ignoresSiblings) || !values.Remove(keyword, out value))
            {
                value = default;
                return false;
            }

            location = at.Keyword(keyword);
            return true;
        }

        public string? String(string keyword)
        {
            if (!Take(keyword, out JsonElement value, out string location))
            {
                return null;
            }

            return value.ValueKind == JsonValueKind.String ? Text(value, location) : throw Invalid(location, "a string");
        }

        public bool? Boolean(string keyword)
        {
            if (!Take(keyword, out JsonElement value, out string location))
            {
                return null;
            }

            return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw Invalid(location, "a boolean");
        }

        public JsonElement? Element(string keyword) => Take(keyword, out JsonElement value, out _) ? value : null;

        public ImmutableArray<JsonElement> Elements(string keyword)
        {
            if (!Take(keyword, out JsonElement value, out string location))
            {
                return [];
            }

            return value.ValueKind == JsonValueKind.Array ? [.. value.EnumerateArray()] : throw Invalid(location, "an array");
        }

        public ImmutableArray<string> Strings(string keyword) =>
            Take(keyword, out JsonElement value, out string location) ? ToUniqueStrings(value, location) : [];

        public JsonElement? Number(string keyword)
        {
            if (!Take(keyword, out JsonElement value, out string location))
            {
                return null;
            }

            return value.ValueKind == JsonValueKind.Number ? value : throw Invalid(location, "a number");
        }

        public JsonElement? PositiveNumber(string keyword)
        {
            if (!Take(keyword, out JsonElement value, out string location))
            {
                return null;
            }

            bool isPositive = value.ValueKind == JsonValueKind.Number && ExactNumber.Parse(value.GetRawText()).CompareTo(ExactNumber.Parse("0")) > 0;
            return isPositive ? value : throw Invalid(location, "a number above zero");
        }

        public int? Count(string keyword)
        {
            if (!Take(keyword, out JsonElement value, out string location))
            {
                return null;
            }

            if (value.ValueKind != JsonValueKind.Number)
            {
                throw Invalid(location, "a non-negative integer");
            }

            ExactNumber number = ExactNumber.Parse(value.GetRawText());

            if (!number.IsInteger || number.CompareTo(ExactNumber.Parse("0")) < 0)
            {
                throw Invalid(location, "a non-negative integer");
            }

            if (number.CompareTo(ExactNumber.Parse("2147483647")) > 0)
            {
                throw Error(JsonSchemaImportCodes.LimitExceeded, location, $"{keyword} is larger than Zorg can represent.");
            }

            return (int)value.GetDouble();
        }

        public string? Pattern(string keyword)
        {
            string? pattern = String(keyword);

            if (pattern is not null)
            {
                importer.CheckPattern(pattern, at.Keyword(keyword));
            }

            return pattern;
        }

        public SchemaNode? Schema(string keyword, Scope scope) =>
            Take(keyword, out JsonElement value, out _)
                ? importer.Build(value, scope.Child(keyword), allowsBoolean: keyword is "additionalProperties" or "additionalItems")
                : null;

        public ImmutableArray<SchemaNode> List(string keyword, Scope scope)
        {
            if (!Take(keyword, out JsonElement value, out string location))
            {
                return [];
            }

            if (value.ValueKind != JsonValueKind.Array)
            {
                throw Invalid(location, "an array of schemas");
            }

            ImmutableArray<SchemaNode>.Builder nodes = ImmutableArray.CreateBuilder<SchemaNode>(value.GetArrayLength());

            foreach (JsonElement item in value.EnumerateArray())
            {
                nodes.Add(importer.Build(item, scope.Child(keyword, nodes.Count)));
            }

            return nodes.MoveToImmutable();
        }

        public ImmutableArray<SchemaNode> NonEmptyList(string keyword, Scope scope)
        {
            if (Peek(keyword, out JsonElement value) && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0)
            {
                throw Invalid(at.Keyword(keyword), "a non-empty array of schemas");
            }

            return List(keyword, scope);
        }

        public ImmutableArray<SchemaProperty> Named(string keyword, Scope scope) => Named(keyword, scope, checkNames: false);

        public ImmutableArray<SchemaProperty> PatternNamed(string keyword, Scope scope) => Named(keyword, scope, checkNames: true);

        public ImmutableArray<KeyValuePair<string, ImmutableArray<string>>> DependentRequired()
        {
            if (!Take("dependentRequired", out JsonElement value, out string location))
            {
                return [];
            }

            if (value.ValueKind != JsonValueKind.Object)
            {
                throw Invalid(location, "an object");
            }

            return [.. value.EnumerateObject().Select(property =>
            {
                string name = Name(property, location);
                return KeyValuePair.Create(name, ToUniqueStrings(property.Value, $"{location}/{Escape(name)}"));
            })];
        }

        private static JsonSchemaImportException Invalid(string location, string expected) =>
            Error(JsonSchemaImportCodes.InvalidSchema, location, $"Expected {expected}.");

        private ImmutableArray<SchemaProperty> Named(string keyword, Scope scope, bool checkNames)
        {
            if (!Take(keyword, out JsonElement value, out string location))
            {
                return [];
            }

            if (value.ValueKind != JsonValueKind.Object)
            {
                throw Invalid(location, "an object of schemas");
            }

            HashSet<string> seen = new(StringComparer.Ordinal);
            ImmutableArray<SchemaProperty>.Builder properties = ImmutableArray.CreateBuilder<SchemaProperty>();

            foreach (JsonProperty property in value.EnumerateObject())
            {
                string name = Name(property, location);
                Scope child = scope.Child(keyword, name);

                if (!seen.Add(name))
                {
                    throw Error(JsonSchemaImportCodes.InvalidSchema, child.Pointer, "The name appears twice.");
                }

                if (checkNames)
                {
                    importer.CheckPattern(name, child.Pointer);
                }

                properties.Add(new SchemaProperty(name, importer.Build(property.Value, child)));
            }

            return properties.ToImmutable();
        }
    }
}
