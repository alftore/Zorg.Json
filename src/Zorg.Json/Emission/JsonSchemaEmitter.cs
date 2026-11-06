using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Text.Json.Nodes;
using Zorg.Json.Model;
using Zorg.Json.Import;
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
        _objectBranches = new ObjectBranchCheck(_profile.Dialect == JsonSchemaDialect.OpenApi30);
        _rewritten = [.. _profile.Rewriters.Select(_ => new Dictionary<SchemaNode, SchemaNode>(ReferenceEqualityComparer.Instance))];
        _rewrittenNegative = [.. _profile.Rewriters.Select(_ => new Dictionary<SchemaNode, SchemaNode>(ReferenceEqualityComparer.Instance))];
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
    private readonly ObjectBranchCheck _objectBranches;
    private readonly Dictionary<SchemaNode, SchemaNode>[] _rewritten;
    private readonly Dictionary<SchemaNode, SchemaNode>[] _rewrittenNegative;
    private List<JsonObject>? _fragments;
    private ImportScope? _import;
    private ImportScope? _looseScope;
    private readonly List<Site> _sites = [];
    private readonly List<LeavingDefinition> _leaving = [];
    private Dictionary<JsonObject, SchemaNode>? _resources;
    private const string DocumentBase = SchemaImporter.DocumentBase;

    private const string DocumentOrigin = SchemaImporter.DocumentOrigin;

    private readonly Dictionary<JsonObject, string> _writtenBases = new(ReferenceEqualityComparer.Instance);

    /// <summary>The base URI of what is being written.</summary>
    private string _base = DocumentBase;

    private readonly Dictionary<ImmutableArray<SchemaNode>, bool> _overlapAnswers = [];

    private HashSet<string>? _ids;
    private HashSet<string>? _idTexts;
    private HashSet<string>? _floatingIds;
    private List<(JsonObject Schema, string Anchor)>? _anchors;
    private Pointing? _pointing;
    private bool _writingHome;

    /// <summary>The home being written sits in another resource than what it copies, where a dynamic name would read differently.</summary>
    private bool _homeIsAway;
    private bool _isUnread;
    private int _emitted;
    private int _depth;

    /// <summary>A node written outside the root of its document, as when a rewriter states one subschema as text, still has places.</summary>
    private ImportScope Scope => _import ?? (_looseScope ??= new ImportScope(null));

    private bool IsInput => _options.Direction == SchemaDirection.Input;

    private bool IsDraft07 => _profile.Dialect == JsonSchemaDialect.Draft07;

    private bool IsOpenApi30 => _profile.Dialect == JsonSchemaDialect.OpenApi30;

    /// <summary>
    /// The dynamic scope needs resources that stay what they are, in a dialect that has it. Inside another schema an import shares its
    /// resource with what surrounds it, so its anchors could clash and its scope would change.
    /// </summary>
    private bool LeavesOutDynamic => IsDraft07 || IsOpenApi30 || _options.ReferencePrefix is not null || !_profile.Rewriters.IsEmpty || !_isWholeImport || _homeIsAway;

    /// <summary>True when the document being written is one imported document, as it was.</summary>
    private bool _isWholeImport;

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
            result = Apply(index, result, isRoot, negative: false, depth: 0);
        }

        return result;
    }

    /// <summary>
    /// A subschema used in many places is rewritten once, because a rewriter's answer for a node doesn't depend on where the
    /// node is. Without that, a schema that uses the same schema twice, again and again, takes exponential time before the
    /// emitter gets to count anything.
    /// </summary>
    private SchemaNode Apply(int index, SchemaNode node, bool isRoot, bool negative, int depth)
    {
        if (depth > MaxDepth)
        {
            throw TooDeep();
        }

        RuntimeHelpers.EnsureSufficientExecutionStack();

        Dictionary<SchemaNode, SchemaNode> memo = negative ? _rewrittenNegative[index] : _rewritten[index];

        if (!isRoot && memo.TryGetValue(node, out SchemaNode? known))
        {
            return known;
        }

        // Below a negative position everything stays negative, since a rewriter can't tell which not cancels which.
        SchemaNode mapped = node.MapChildren((child, inside) => Apply(index, child, isRoot: false, negative || inside, depth + 1));
        SchemaNode result = _profile.Rewriters[index].Rewrite(mapped, new RewriteContext(_options, isRoot, _diagnostics.Add) { InNegativePosition = negative });

        if (!isRoot)
        {
            memo[node] = result;
        }

        return result;
    }

    private EmitResult EmitRoot(SchemaNode node)
    {
        _isWholeImport = node.Behavior.Location is { Length: 0 };
        JsonNode emitted = EmitNode(node, string.Empty);
        JsonObject schema = emitted as JsonObject ?? BooleanRoot(emitted);

        if (_profile.WritesSchemaKeyword)
        {
            schema.Insert(0, "$schema", _profile.MetaSchema);
        }

        if (_definitions.Count > 0 && _options.ReferencePrefix is null)
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

            _definitions.Clear();
        }

        WriteReferences(schema, node);

        ImmutableDictionary<string, JsonObject> definitions = _definitions
            .ToImmutableDictionary(entry => entry.Key, entry => entry.Value as JsonObject ?? BooleanRoot(entry.Value), StringComparer.Ordinal);

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

    /// <summary>
    /// Writes every reference of an imported document, now that the document exists. A target is read where it went, so a keyword
    /// that was respelled or a place that moved needs nothing here. A target that wasn't written, or that this reference can't reach,
    /// gets a home under the definitions: the dialect may leave out <c>not</c>, and text under an unknown keyword is never a schema.
    /// </summary>
    private void WriteReferences(JsonObject root, SchemaNode rootNode)
    {
        if (_sites.Count == 0 && _anchors is null && _leaving.Count == 0)
        {
            return;
        }

        _pointing = new Pointing();

        // The limit guards against a schema that doubles. Homes are written once each, and have a limit of their own.
        _emitted = 0;

        // Writing a home can add references of its own.
        for (int index = 0; index < _sites.Count; index++)
        {
            Site site = _sites[index];
            site.Owner = Owner(site.Holder);
            site.Place = FindPlace(site) ?? WriteHome(site, root, rootNode);
        }

        DropRepeatedAnchors();

        if (_leaving.Count > 0 && _options.DefinitionTagLength > 0)
        {
            // The hash reads every reference as it will be written, and those to a definition being named as that definition's hash.
            foreach (Site site in _sites)
            {
                site.Holder[site.Keyword] = Spell(site);
            }

            NameLeavingDefinitions();

            foreach (Site site in _sites)
            {
                site.Pointer = null;
            }
        }

        foreach (Site site in _sites)
        {
            site.Holder[site.Keyword] = site.Keyword == "$ref" ? Spell(site) : DynamicSpelling(site);
        }
    }

    /// <summary>The URI of where the target went, and the name it has to end in to stay dynamic.</summary>
    private string DynamicSpelling(Site site)
    {
        string spelled = Spell(site);
        int end = spelled.IndexOf('#', StringComparison.Ordinal);
        int name = site.Text.IndexOf('#', StringComparison.Ordinal);
        return (end < 0 ? spelled : spelled[..end]) + site.Text[name..];
    }

    /// <summary>What writing the references needs for as long as it takes.</summary>
    private sealed class Pointing
    {
        public Dictionary<(JsonObject? Owner, ImportScope Scope, string Location), JsonNode> Homes { get; } = [];

        public Dictionary<JsonObject, int> HomeCounts { get; } = new(ReferenceEqualityComparer.Instance);

        public Dictionary<JsonObject, string> Anchors { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>A node doesn't know its name without searching its parent, so each parent is read once.</summary>
        public Dictionary<JsonNode, string> Tokens { get; } = new(ReferenceEqualityComparer.Instance);

        public Dictionary<JsonNode, string> Definitions { get; } = new(ReferenceEqualityComparer.Instance);

        public string Token(JsonNode node, JsonNode parent)
        {
            if (Tokens.TryGetValue(node, out string? token))
            {
                return token;
            }

            if (parent is JsonObject members)
            {
                foreach ((string name, JsonNode? child) in members)
                {
                    if (child is not null)
                    {
                        Tokens[child] = FragmentEscape(Escape(name));
                    }
                }
            }
            else if (parent is JsonArray items)
            {
                for (int index = 0; index < items.Count; index++)
                {
                    if (items[index] is { } child)
                    {
                        Tokens[child] = index.ToString(CultureInfo.InvariantCulture);
                    }
                }
            }

            return Tokens[node];
        }
    }

    /// <summary>The resource a node is read from, or null for the document.</summary>
    private JsonObject? Owner(JsonNode node)
    {
        for (JsonNode? current = node; current is not null; current = current.Parent)
        {
            if (current is JsonObject resource && _resources?.ContainsKey(resource) == true)
            {
                return resource;
            }
        }

        return null;
    }

    private JsonNode? FindPlace(Site site)
    {
        string? location = site.Target().Behavior.Location;

        if (location is null)
        {
            return null;
        }

        if (_pointing!.Homes.TryGetValue((site.Owner, site.Scope, location), out JsonNode? home))
        {
            return home;
        }

        if (site.Scope.Find(location) is not { } found)
        {
            return null;
        }

        if (!site.Text.StartsWith('#') && _options.ReferencePrefix is null && !IsOpenApi30 && UriReference(site, found) is { } spelled)
        {
            site.Pointer = spelled;
            return found;
        }

        if (Pointer(found, site.Owner) is not { } pointer)
        {
            return null;
        }

        JsonNode place = found;

        site.Pointer = pointer;
        return place;
    }

    /// <summary>The base URI a written resource ended up with, or that of the document.</summary>
    private string BaseOf(JsonObject? owner) =>
        owner is not null && _writtenBases.TryGetValue(owner, out string? written) ? written : DocumentBase;

    /// <summary>A URI resolved against a base, or null when it can't be read.</summary>
    private static string? Resolve(string baseUri, string reference) =>
        SchemaImporter.SplitUri(baseUri, reference) is { } resolved ? resolved.Uri : null;

    /// <summary>
    /// Spells a reference from the resource its target ended up in, which may have another <c>$id</c> than the document had, and
    /// the pointer from there. The text of the document stays when it still reads the same. Null when the place can't be reached by URI.
    /// </summary>
    private string? UriReference(Site site, JsonNode place)
    {
        JsonObject? holder = Owner(place);
        string target = BaseOf(holder);
        string from = BaseOf(site.Owner);

        if (Pointer(place, holder) is not { } within)
        {
            return null;
        }

        if (SchemaImporter.SplitUri(from, site.Text) is { } text && text.Uri == target
            && (Decoded(text.Fragment) == Decoded(within) || (place is JsonObject schema && _pointing!.Anchors.TryGetValue(schema, out string? anchor) && text.Fragment == "#" + anchor)))
        {
            return site.Text;
        }

        if (target == from)
        {
            return within;
        }

        string? uri = target;

        if (target.StartsWith(DocumentOrigin, StringComparison.Ordinal))
        {
            // The document has no base of its own, so only a relative reference can reach this.
            uri = from.StartsWith(DocumentOrigin, StringComparison.Ordinal) ? Relative(from, target) : null;
        }

        return uri is null ? null : within == "#" ? uri : uri + within;
    }

    private static string? Relative(string from, string target)
    {
        string relative = new Uri(from).MakeRelativeUri(new Uri(target)).OriginalString;

        foreach (string candidate in new[] { relative, "./" })
        {
            if (candidate.Length > 0 && Resolve(from, candidate) == target)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>The pointer to a place, from a resource or the document. Null when it can't be reached from there.</summary>
    private string? Pointer(JsonNode place, JsonObject? owner)
    {
        Stack<string> tokens = new();
        JsonNode current = place;

        while (!ReferenceEquals(current, owner))
        {
            if (current.Parent is not { } parent)
            {
                return owner is null ? Top(current, tokens) : null;
            }

            tokens.Push(_pointing!.Token(current, parent));
            current = parent;
        }

        return Join("#", tokens);
    }

    /// <summary>With a prefix, a definition is in a document of the caller's, and the root goes wherever the caller puts it, so nothing can point at the root.</summary>
    private string? Top(JsonNode top, Stack<string> tokens)
    {
        if (_options.ReferencePrefix is null)
        {
            return Join("#", tokens);
        }

        Dictionary<JsonNode, string> known = _pointing!.Definitions;

        if (known.Count < _definitions.Count)
        {
            foreach ((string name, JsonNode definition) in _definitions)
            {
                known[definition] = name;
            }
        }

        return known.TryGetValue(top, out string? found) ? Join(ReferencePrefix + FragmentEscape(Escape(found)), tokens) : null;
    }

    private static string Join(string start, Stack<string> tokens) =>
        tokens.Count == 0 ? start : start + "/" + string.Join('/', tokens);

    private JsonNode WriteHome(Site site, JsonObject root, SchemaNode rootNode)
    {
        SchemaNode target = site.Target();
        string location = target.Behavior.Location ?? string.Empty;
        bool leavesDocument = site.Owner is null && _options.ReferencePrefix is not null;
        JsonObject owner = site.Owner ?? root;
        JsonObject definitions = owner[_profile.DefinitionsKeyword] as JsonObject ?? [];

        // The document may hold a definition by this name already, which a reference of its own is reading.
        string name = UniqueName(HomeName(location) ?? NextHomeName(owner), leavesDocument ? null : definitions);
        ImportScope? outer = _import;
        _import = site.Scope;
        _isUnread = false;
        _definitions[name] = new JsonObject();
        _base = BaseOf(site.Owner);
        _writingHome = true;
        SchemaNode around = site.Owner is null ? rootNode : _resources![site.Owner];
        _homeIsAway = !ReferenceEquals(target.Behavior.Resource, around.Behavior.Resource);
        JsonNode definition = EmitNode(Rewrite(target, isRoot: false) with { Defs = [] }, $"/{_profile.DefinitionsKeyword}/{Escape(name)}");
        _homeIsAway = false;
        _writingHome = false;
        _import = outer;
        _pointing!.Homes[(site.Owner, site.Scope, location)] = definition;

        if (leavesDocument)
        {
            _definitions[name] = definition;
            _pointing.Definitions[definition] = name;
            _leaving.Add(new LeavingDefinition(name, HomeName(location) ?? "target", location));
        }
        else
        {
            _definitions.Remove(name);
            definitions[name] = definition;
            _pointing.Tokens[definition] = FragmentEscape(Escape(name));

            if (!owner.ContainsKey(_profile.DefinitionsKeyword))
            {
                owner.Insert(FirstExtension(owner, site.Owner is null ? rootNode : _resources![owner]), _profile.DefinitionsKeyword, definitions);
            }
        }

        return definition;
    }

    /// <summary>
    /// Definitions that leave the document join those of other documents, in an order nobody here can see, so what's written
    /// under a name must follow from the name. Each name ends in a hash of the definition and of everything it reaches, and of nothing
    /// around it. The same definition has the same name wherever it's used, and two different ones never share it. A cycle is hashed as one.
    /// </summary>
    private void NameLeavingDefinitions()
    {
        Dictionary<JsonNode, LeavingDefinition> byNode = new(ReferenceEqualityComparer.Instance);

        foreach (LeavingDefinition leaving in _leaving)
        {
            leaving.Node = _definitions[leaving.Name];
            byNode[leaving.Node] = leaving;
        }

        foreach (Site site in _sites)
        {
            if (site.Owner is null && byNode.TryGetValue(site.Holder.Root, out LeavingDefinition? from) && byNode.TryGetValue(site.Place!.Root, out LeavingDefinition? to))
            {
                string tail = (site.Pointer ?? Pointer(site.Place, null)!)[(ReferencePrefix.Length + FragmentEscape(Escape(to.Name)).Length)..];
                from.References.Add((site, to, tail));
                from.Targets.Add(to);
            }
        }

        foreach (List<LeavingDefinition> component in StrongComponents.Find(_leaving, leaving => leaving.Targets))
        {
            Hash(component);
        }

        foreach (LeavingDefinition leaving in _leaving)
        {
            _definitions.Remove(leaving.Name);
        }

        // The same hash is the same content, so a schema used twice in a document is written once. Different content that shares a tag gets the whole hash.
        Dictionary<string, string> names = new(StringComparer.Ordinal);

        foreach (IGrouping<string, LeavingDefinition> tagged in _leaving.GroupBy(leaving => $"{leaving.Label}-{leaving.Hash![.._options.DefinitionTagLength]}", StringComparer.Ordinal))
        {
            bool isClash = tagged.Select(leaving => leaving.Hash).Distinct(StringComparer.Ordinal).Skip(1).Any();

            foreach (LeavingDefinition leaving in tagged)
            {
                if (!names.TryGetValue(leaving.Hash!, out string? name))
                {
                    name = names[leaving.Hash!] = UniqueName(isClash ? $"{leaving.Label}-{leaving.Hash}" : tagged.Key);
                    _definitions[name] = leaving.Node!;
                }

                _pointing!.Definitions[leaving.Node!] = name;
            }
        }
    }

    private static void Hash(List<LeavingDefinition> component)
    {
        // Sorted by what the document says, because the order of the walk depends on where it came in.
        component.Sort(static (left, right) => (string.CompareOrdinal(left.Location, right.Location), string.CompareOrdinal(left.Label, right.Label)) switch
        {
            (not 0 and var location, _) => location,
            (_, var label) => label,
        });

        StringBuilder text = new();

        for (int index = 0; index < component.Count; index++)
        {
            component[index].Position = index;
        }

        foreach (LeavingDefinition member in component)
        {
            foreach ((Site site, LeavingDefinition to, string tail) in member.References)
            {
                site.Holder["$ref"] = $"#{to.Hash ?? to.Position.ToString(CultureInfo.InvariantCulture) + to.Label}{tail}";
            }

            text.Append(member.Label).Append(' ').Append(member.Node!.ToJsonString()).Append('\n');
        }

        string whole = Sha(text.ToString());

        foreach (LeavingDefinition member in component)
        {
            member.Hash = Sha(whole + member.Position.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>A definition that goes to <see cref="EmitResult.Definitions"/>: its name while the document is written, and what its final name is made of.</summary>
    private sealed class LeavingDefinition(string name, string label, string location)
    {
        public string Name { get; } = name;

        public string Label { get; } = label;

        public string Location { get; } = location;

        public JsonNode? Node { get; set; }

        public List<(Site Site, LeavingDefinition To, string Tail)> References { get; } = [];

        public List<LeavingDefinition> Targets { get; } = [];

        public int Position { get; set; }

        public string? Hash { get; set; }
    }

    /// <summary>A target that was a definition of its document keeps its name.</summary>
    private static string? HomeName(string location)
    {
        string[] tokens = location.Split('/');

        return tokens switch
        {
            ["", "$defs" or "definitions", var name] => Unescape(name),
            ["", "components", "schemas", var name] => Unescape(name),
            _ => null,
        };
    }

    private static string Unescape(string token) =>
        token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

    private string NextHomeName(JsonObject owner)
    {
        Dictionary<JsonObject, int> counts = _pointing!.HomeCounts;
        int number = counts[owner] = counts.GetValueOrDefault(owner) + 1;
        return "target-" + number.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>A schema written twice repeats its anchors, and a resource can't have two of a name. References to the later ones are written as pointers.</summary>
    private void DropRepeatedAnchors()
    {
        HashSet<(JsonObject?, string)> seen = [];

        foreach ((JsonObject schema, string anchor) in _anchors ?? [])
        {
            if (seen.Add((Owner(schema), anchor)))
            {
                _pointing!.Anchors[schema] = anchor;
            }
            else if (schema["$id"]?.GetValue<string>() is { } id && id.EndsWith("#" + anchor, StringComparison.Ordinal))
            {
                if (id.Length == anchor.Length + 1)
                {
                    schema.Remove("$id");
                }
                else
                {
                    schema["$id"] = id[..^(anchor.Length + 1)];
                }
            }
            else
            {
                schema.Remove("$anchor");
            }
        }
    }

    /// <summary>A reference that still reads true keeps the text of its document, so that what didn't move doesn't change.</summary>
    private string Spell(Site site)
    {
        if (site.Place is JsonObject schema && _pointing!.Anchors.TryGetValue(schema, out string? anchor) && site.Text == "#" + anchor && ReferenceEquals(Owner(schema), site.Owner))
        {
            return site.Text;
        }

        string pointer = site.Pointer ?? Pointer(site.Place!, site.Owner)!;
        return pointer == site.Text || Decoded(pointer) == Decoded(site.Text) ? site.Text : pointer;
    }

    private static string Decoded(string reference) => Uri.UnescapeDataString(reference);

    /// <summary>What 2020-12 allows in an anchor: <c>^[A-Za-z_][-A-Za-z0-9._]*$</c>.</summary>
    private static bool IsPlainAnchor(string anchor) =>
        anchor.Length > 0
        && (char.IsAsciiLetter(anchor[0]) || anchor[0] == '_')
        && anchor.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_');

    /// <summary>A reference of an imported document: the object that holds it, what it first said, and what it means.</summary>
    private sealed class Site(JsonObject holder, string text, Func<SchemaNode> target, ImportScope scope)
    {
        public JsonObject Holder { get; set; } = holder;

        public string Text { get; } = text;

        public Func<SchemaNode> Target { get; } = target;

        public ImportScope Scope { get; } = scope;

        public JsonObject? Owner { get; set; }

        public JsonNode? Place { get; set; }

        public string? Pointer { get; set; }

        public string Keyword { get; init; } = "$ref";
    }

    /// <summary>Where the extensions start in what was written. Not every extension is written, and a reference is written without any.</summary>
    private static int FirstExtension(JsonObject schema, SchemaNode node)
    {
        HashSet<string> names = new(node.Extensions.Select(extension => extension.Key), StringComparer.Ordinal);
        int index = 0;

        foreach ((string keyword, JsonNode? _) in schema)
        {
            if (names.Contains(keyword) || keyword is "x-inert-nullable" or "x-inert-definitions")
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
            JsonNode written = IsOpenApi30 ? (allowed ? new JsonObject() : new JsonObject { ["not"] = new JsonObject() }) : JsonValue.Create(allowed);
            Place(node, written);
            return written;
        }

        string? name = node.Behavior.DefinitionName;

        // An imported node with a name has a reference of its own, which belongs to its document.
        if (name is not null && node.Behavior.Location is null && node.Behavior.Target is { } target)
        {
            return new JsonObject { ["$ref"] = Reference(target(), name) };
        }

        if (name is not null && location.Length > 0 && _options.Reused == ReusedSchemaHandling.Ref)
        {
            return new JsonObject { ["$ref"] = Reference(node, name) };
        }

        return EmitObject(node, location);
    }

    private string UniqueName(string requested, JsonObject? written = null)
    {
        string name = requested;

        for (int suffix = 2; _definitions.ContainsKey(name) || written?.ContainsKey(name) == true; suffix++)
        {
            name = requested + suffix.ToString(CultureInfo.InvariantCulture);
        }

        return name;
    }

    private string Reference(SchemaNode target, string requested)
    {
        if (!_definitionNames.TryGetValue(target, out string? name))
        {
            name = UniqueName(requested);
            _definitionNames.Add(target, name);
            _definitions[name] = new JsonObject();

            // A target that's a reference itself must register its own definition. Anything else is written here,
            // because EmitNode would refer a named schema back to this very definition.
            string location = $"/{_profile.DefinitionsKeyword}/{Escape(name)}";
            SchemaNode rewritten = Rewrite(target, isRoot: false);
            _definitions[name] = rewritten.Behavior.Target is null || rewritten.Behavior.Location is not null ? EmitObject(rewritten, location) : EmitNode(rewritten, location);
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
        ImportScope? outerImport = _import;
        bool isHome = _writingHome;
        _writingHome = false;
        _fragments = null;

        // OpenAPI 3.0 reads neither, and a definition that leaves the document joins others, where a pointer has to read from the
        // whole document. So an imported node loses them there.
        if ((IsOpenApi30 || _options.ReferencePrefix is not null) && source.Behavior.Location is not null)
        {
            bool keepsId = location.Length == 0 && _options.ReferencePrefix is null;
            source = source with { Id = keepsId ? source.Id : null, Anchor = null };
        }

        string outerBase = _base;
        bool isResource = source.Id is { } id && !id.StartsWith('#');
        bool isImportRoot = source.Behavior.Location is { Length: 0 } && !isHome;

        // A reference is to the copy of its target in the same import. A resource written again reads its own references from the copy.
        if (isImportRoot)
        {
            _import = new ImportScope(null);
        }
        else if (isResource && isHome)
        {
            _import = new ImportScope(Scope);
        }

        try
        {
            JsonObject written = EmitFields(isImportRoot && IsEmbeddedImport(source, location) ? EnterImport(source) : source, location);

            if (isResource)
            {
                (_resources ??= new(ReferenceEqualityComparer.Instance)).Add(written, source);
            }

            Place(source, written);
            return written;
        }
        finally
        {
            _fragments = outer;
            _import = outerImport;
            _base = outerBase;
        }
    }

    /// <summary>Remembers where a node of an imported document went.</summary>
    private void Place(SchemaNode source, JsonNode written)
    {
        if (source.Behavior.Location is { } place && !_isUnread)
        {
            Scope.Places.TryAdd(place, written);
        }
    }

    /// <summary>
    /// The root of an imported document holds its definitions. Written inside another schema, or with a
    /// <see cref="JsonSchemaOptions.ReferencePrefix"/> that keeps definitions elsewhere, they go where the definitions of the whole document go.
    /// With an id of its own the root is a resource, and its definitions stay in it.
    /// </summary>
    private bool IsEmbeddedImport(SchemaNode node, string location) =>
        (location.Length > 0 || _options.ReferencePrefix is not null)
        && !(node.Id is { } id && !id.StartsWith('#'));

    /// <summary>Moves the definitions of an imported root to the definitions of the whole document, under names that are free there.</summary>
    private SchemaNode EnterImport(SchemaNode root)
    {
        Dictionary<string, string> names = new(StringComparer.Ordinal);

        foreach (SchemaProperty definition in root.Defs)
        {
            string name = UniqueName(definition.Name);
            names[definition.Name] = name;
            _definitions[name] = new JsonObject();

            if (_options.ReferencePrefix is not null)
            {
                _leaving.Add(new LeavingDefinition(name, definition.Name, definition.Schema.Behavior.Location ?? string.Empty));
            }
        }

        foreach (SchemaProperty definition in root.Defs)
        {
            string name = names[definition.Name];
            _definitions[name] = EmitNode(definition.Schema, $"/{_profile.DefinitionsKeyword}/{Escape(name)}");
        }

        return root.Defs.IsEmpty ? root : root with { Defs = [] };
    }

    /// <summary>Where the nodes of one imported document went, in one copy of it. A resource inside it has a scope of its own.</summary>
    private sealed class ImportScope(ImportScope? outer)
    {
        public ImportScope? Outer { get; } = outer;

        /// <summary>Keyed by the instance: every copy of a node shares its location, and hashing long pointers shows in a large emit.</summary>
        public Dictionary<string, JsonNode> Places { get; } = new(ReferenceEqualityComparer.Instance);

        public JsonNode? Find(string location)
        {
            for (ImportScope? scope = this; scope is not null; scope = scope.Outer)
            {
                if (scope.Places.TryGetValue(location, out JsonNode? place))
                {
                    return place;
                }
            }

            return null;
        }
    }

    /// <summary>A second resource can't have the id of the first, and a copy reads its references from itself, so it gets an id of its own.</summary>
    private string UniqueId(string requested)
    {
        string id = requested;

        // Ids are compared as URIs: two nested ones with the same text are different under different bases.
        // The base of a document with no $id isn't known, so there the same text may turn out to be the same URI.
        for (int suffix = 2; !IsFreeId(id); suffix++)
        {
            id = requested.TrimEnd('#') + "-" + suffix.ToString(CultureInfo.InvariantCulture);
        }

        return id;
    }

    private bool IsFreeId(string id)
    {
        string text = id.TrimEnd('#');
        string uri = Resolve(_base, id) ?? text;
        bool floats = uri.StartsWith(DocumentOrigin, StringComparison.Ordinal);
        _ids ??= new(StringComparer.Ordinal);
        _floatingIds ??= new(StringComparer.Ordinal);
        _idTexts ??= new(StringComparer.Ordinal);

        if (_ids.Contains(uri) || _floatingIds.Contains(text) || (floats && _idTexts.Contains(text)))
        {
            return false;
        }

        _ids.Add(uri);
        _idTexts.Add(text);

        if (floats)
        {
            _floatingIds.Add(text);
        }

        return true;
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
        SchemaNode node = PlainReference(IsInput ? RelocateCounts(Relocate(written, notes, location), notes, location) : written);
        JsonObject schema = [];

        string? anchor = node.Anchor;
        string? id = node.Id is null ? null : node.Id.StartsWith('#') ? node.Id : UniqueId(node.Id);
        Add(schema, "$id", id);

        if (id is not null && !id.StartsWith('#') && Resolve(_base, id) is { } resolved)
        {
            _base = resolved;
            _writtenBases[schema] = resolved;
        }

        // 2019-09 allows more in a name than 2020-12 does. A reference to it is written as a pointer.
        if (!IsDraft07 && anchor is not null && !IsPlainAnchor(anchor))
        {
            anchor = null;
        }

        if (IsDraft07 && anchor is not null)
        {
            // Draft-07 spells the base and the anchor as one: http://x/y#anchor.
            schema["$id"] = id?.TrimEnd('#') + "#" + anchor;
        }
        else
        {
            Add(schema, "$anchor", anchor);
        }

        if (anchor is not null)
        {
            (_anchors ??= []).Add((schema, anchor));
        }

        Site? site = null;

        if (node.Ref is not null && node.Behavior.Target is { } referent)
        {
            // The text is written once the whole document is, when it's known where the target went.
            site = new Site(schema, node.Ref, referent, Scope);
            _sites.Add(site);
            schema["$ref"] = node.Ref;
        }
        else if (node.Ref is not null)
        {
            schema["$ref"] = WrittenReference(node.Ref);
        }

        if (LeavesOutDynamic)
        {
            UnsupportedIf(node.DynamicRef is not null, "$dynamicRef", notes, location);
        }
        else
        {
            Add(schema, "$dynamicAnchor", node.DynamicAnchor);

            // In its own resource the name is all it says. Another resource is read by URI, and that can change.
            if (node.DynamicRef is { } dynamicReference && dynamicReference.StartsWith('#'))
            {
                Add(schema, "$dynamicRef", dynamicReference);
            }
            else if (node.DynamicRef is { } remote && node.Behavior.Target is { } dynamicTarget)
            {
                _sites.Add(new Site(schema, remote, dynamicTarget, Scope) { Keyword = "$dynamicRef" });
                schema["$dynamicRef"] = remote;
            }
        }

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
            else if (keyword == "$schema" && value.ValueKind == JsonValueKind.String && value.GetString() != _profile.MetaSchema)
            {
                // Bundled resources repeat it, and what's written beside it now follows the target.
                schema["x-inert-$schema"] = ToNode(value);
            }
            else if (IsDraft07 && keyword == "definitions")
            {
                // 2020-12 ignores it, but draft-07 holds its entries to the rules of a schema. A reference into it is written again.
                schema["x-inert-definitions"] = ToNode(value);
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

        if ((IsDraft07 || IsOpenApi30) && schema.ContainsKey("$ref") && schema.Count > 1)
        {
            JsonObject moved = WrapReference(schema, applicatorsIndex);

            if (site is not null)
            {
                site.Holder = moved;
            }
        }

        return schema;
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

    /// <summary>Before 2019-09, keywords beside <c>$ref</c> are ignored, so <c>$ref</c> moves into an <c>allOf</c>. Says where it went.</summary>
    private static JsonObject WrapReference(JsonObject schema, int applicatorsIndex)
    {
        JsonObject moved = new() { ["$ref"] = schema["$ref"]!.DeepClone() };
        schema.Remove("$ref");

        if (schema["allOf"] is JsonArray allOf)
        {
            allOf.Insert(0, moved);
        }
        else
        {
            schema.Insert(applicatorsIndex - 1, "allOf", new JsonArray(moved));
        }

        return moved;
    }

    /// <summary>A reference with no target, which only a hand-built node has, is written as it is, with the definitions where they go.</summary>
    private string WrittenReference(string reference) => reference.Replace("#/$defs/", ReferencePrefix, StringComparison.Ordinal);

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
        if (IsInput && node.Format is { } format && node.Behavior.FormatIsAnnotation && (IsDraft07 || IsOpenApi30) && StringFormats.IsKnown(format))
        {
            // Validators of these dialects may check format, and parsing doesn't, so writing it could reject what parsing accepts.
            Unrepresentable("format", "Parsing doesn't check this format and a validator of this dialect may, so it's kept as x-inert-format. Import with Formats = FormatHandling.AssertKnown to state it.", location);
            schema["x-inert-format"] = format;
        }
        else
        {
            Add(schema, "format", node.Format ?? NumberFormat(node));
        }
        Add(schema, "contentEncoding", node.ContentEncoding);
        Add(schema, "contentMediaType", node.ContentMediaType);

        if (node.ContentSchema is not null)
        {
            // Draft-07 and OpenAPI 3.0 read it as an unknown keyword, so it isn't a place a reference can go.
            bool outer = _isUnread;
            _isUnread = outer || IsDraft07 || IsOpenApi30;
            schema["contentSchema"] = EmitNode(node.ContentSchema, $"{location}/contentSchema");
            _isUnread = outer;
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

            if (node.MaxContains is not null && node.Contains is not null && IsInput && !_exactness.IsExact(node.Contains, out SchemaExactness.Cause? cause))
            {
                UnsupportedBecause("maxContains", cause, $"{location}/contains", notes, location);
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

                // Draft-07 has one value for a name, so a list and a schema both apply as the branches of an allOf.
                if (dependentRequired.TryGetPropertyValue(name, out JsonNode? list) && list is not null)
                {
                    dependentRequired[name] = new JsonObject { ["allOf"] = new JsonArray(new JsonObject { ["required"] = list.DeepClone() }, dependency) };
                }
                else
                {
                    dependentRequired[name] = dependency;
                }
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
            if (IsInput && !_exactness.IsExact(node.Not, out SchemaExactness.Cause? cause))
            {
                UnsupportedBecause("not", cause, $"{location}/not", notes, location);
            }
            else
            {
                schema["not"] = EmitNode(node.Not, $"{location}/not");
            }
        }

        if (node.If is not null || node.Then is not null || node.Else is not null)
        {
            SchemaExactness.Cause? cause = null;

            if (IsOpenApi30)
            {
                Unsupported("if", notes, location);
            }
            else if (IsInput && node.If is not null && !_exactness.IsExact(node.If, out cause))
            {
                UnsupportedBecause("if", cause, $"{location}/if", notes, location);
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
            // Subschemas of an if that is left out evaluate nothing in the written schema, which would make these stricter than parsing.
            bool lost = (node.UnevaluatedItems is not null || node.UnevaluatedProperties is not null) && IsInput && LosesEvaluations(node);

            if (lost)
            {
                UnsupportedIf(node.UnevaluatedItems is not null, "unevaluatedItems", notes, location);
                UnsupportedIf(node.UnevaluatedProperties is not null, "unevaluatedProperties", notes, location);
            }
            else
            {
                WriteSingle(schema, "unevaluatedItems", node.UnevaluatedItems, location);
                WriteSingle(schema, "unevaluatedProperties", node.UnevaluatedProperties, location);

                if (node.Behavior.ContainsEvaluatesNothing && node.UnevaluatedItems is not null)
                {
                    Unrepresentable("unevaluatedItems", "In 2019-09 contains evaluates nothing, but it does in what's written here, so the written schema can accept more.", location);
                }
            }
        }
    }

    /// <summary>
    /// Branches that aren't exact can overlap in the emitted schema though only one matches in Zorg, so
    /// <c>oneOf</c> relaxes to <c>anyOf</c>. Discriminated branches can't overlap, whatever else they leave out, and
    /// nor can branches that exclude each other by type or by what they require.
    /// </summary>
    private void WriteOneOf(JsonObject schema, SchemaNode node, List<string> notes, string location)
    {
        SchemaExactness.Cause? cause = null;
        string where = location;

        if (IsInput)
        {
            for (int i = 0; i < node.OneOf.Length && cause is null; i++)
            {
                if (!_exactness.IsExact(node.OneOf[i], out cause))
                {
                    where = $"{location}/oneOf/{i}";
                }
            }
        }

        if (cause is null || DiscriminatorKeys.TryBuild(node, out _) || CannotOverlap(node.OneOf))
        {
            WriteList(schema, "oneOf", node.OneOf, location);
        }
        else if (node.AnyOf.IsEmpty)
        {
            Unrepresentable("oneOf", $"Branches can overlap in a validator that doesn't apply every rule, so oneOf is written as anyOf. The rule is {cause.Describe(where)}.", location);

            if (schema.ContainsKey("anyOf"))
            {
                // The types of this node are already in anyOf, and adding to that list would let the branches replace the type limit.
                JsonObject relaxed = [];
                WriteList(relaxed, "anyOf", node.OneOf, location);
                JsonArray all = schema["allOf"] as JsonArray ?? [];
                all.Add((JsonNode)relaxed);
                schema["allOf"] = all;
            }
            else
            {
                WriteList(schema, "anyOf", node.OneOf, location);
            }
        }
        else
        {
            UnsupportedBecause("oneOf", cause, where, notes, location);
        }
    }

    /// <summary>
    /// True when no value can match two branches as they are written, whatever else a branch leaves out, so <c>oneOf</c>
    /// and <c>anyOf</c> agree. Branches share no type, or share only object and exclude each other by what they require.
    /// This judges what is written, so anything that could change it answers false.
    /// </summary>
    private bool CannotOverlap(ImmutableArray<SchemaNode> branches)
    {
        // One answer for each list: the comparisons are budgeted, and what is judged early must agree with what is written later.
        if (!_overlapAnswers.TryGetValue(branches, out bool answer))
        {
            _overlapAnswers[branches] = answer = JudgeOverlap(branches);
        }

        return answer;
    }

    private bool JudgeOverlap(ImmutableArray<SchemaNode> branches)
    {
        // A rewriter can replace a branch or a definition after this is judged.
        if (!_profile.Rewriters.IsEmpty)
        {
            return false;
        }

        JsonTypes[] types = new JsonTypes[branches.Length];
        ObjectBranch?[] objects = new ObjectBranch?[branches.Length];

        for (int i = 0; i < branches.Length; i++)
        {
            types[i] = WrittenTypes(branches[i]);

            if (types[i] == JsonTypes.None)
            {
                return false;
            }

            for (int j = 0; j < i; j++)
            {
                JsonTypes shared = types[i] & types[j];

                if (shared == JsonTypes.None)
                {
                    continue;
                }

                if (shared != JsonTypes.Object)
                {
                    return false;
                }

                objects[i] ??= _objectBranches.Read(branches[i]);
                objects[j] ??= _objectBranches.Read(branches[j]);

                if (objects[i] is not { } left || objects[j] is not { } right || !_objectBranches.Exclude(left, right))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>True when a <c>oneOf</c> has to relax, and <c>anyOf</c> is taken, so <see cref="WriteOneOf"/> leaves it out.</summary>
    private bool OneOfIsLeftOut(SchemaNode node) =>
        !node.OneOf.IsEmpty && !node.AnyOf.IsEmpty && node.OneOf.Any(branch => !_exactness.IsExact(branch))
        && !DiscriminatorKeys.TryBuild(node, out _) && !CannotOverlap(node.OneOf);

    /// <summary>True when a subschema that applies to the same value has an <c>if</c> or a <c>oneOf</c> that isn't written, so what it evaluates is lost.</summary>
    private bool LosesEvaluations(SchemaNode root)
    {
        HashSet<SchemaNode> visited = new(ReferenceEqualityComparer.Instance);
        Stack<SchemaNode> pending = new();
        pending.Push(root);

        while (pending.TryPop(out SchemaNode? node))
        {
            if (!visited.Add(node))
            {
                continue;
            }

            if (visited.Count > SchemaExactness.MaxWalked)
            {
                throw SchemaExactness.TooLarge();
            }

            if (node.If is { } condition && !_exactness.IsExact(condition, out _))
            {
                return true;
            }

            if (OneOfIsLeftOut(node) || (LeavesOutDynamic && node.DynamicRef is not null))
            {
                return true;
            }

            foreach (SchemaNode child in node.AllOf.Concat(node.AnyOf).Concat(node.OneOf).Concat(node.DependentSchemas.Select(entry => entry.Schema)))
            {
                pending.Push(child);
            }

            foreach (SchemaNode? child in new[] { node.If, node.Then, node.Else, node.Behavior.Target?.Invoke() })
            {
                if (child is not null)
                {
                    pending.Push(child);
                }
            }
        }

        return false;
    }

    /// <summary>A <c>$dynamicRef</c> that doesn't land on a <c>$dynamicAnchor</c> of its name is a <c>$ref</c>, and is written as one, so it moves like one.</summary>
    private static SchemaNode PlainReference(SchemaNode node)
    {
        if (node.DynamicRef is not { } reference || node.Behavior.Target is not { } target)
        {
            return node;
        }

        int fragment = reference.IndexOf('#', StringComparison.Ordinal);
        bool isDynamic = fragment >= 0 && target().DynamicAnchor == Uri.UnescapeDataString(reference[(fragment + 1)..]);
        return isDynamic ? node : node with { Ref = reference, DynamicRef = null };
    }

    /// <summary>The types a subschema's written form can accept, or <see cref="JsonTypes.None"/> when that isn't known. Follows references.</summary>
    private JsonTypes WrittenTypes(SchemaNode node)
    {
        HashSet<SchemaNode> followed = new(ReferenceEqualityComparer.Instance);

        while (followed.Add(node) && followed.Count <= SchemaExactness.MaxWalked)
        {
            // Coercion adds string to the written type, and a dynamic scope can lead anywhere.
            if (node.Behavior.Coercion != SchemaCoercion.None || node.DynamicRef is not null)
            {
                return JsonTypes.None;
            }

            // OpenAPI 3.0 has no null type, so a null node with a value list is written without any type.
            if (IsOpenApi30 && node.Types == JsonTypes.Null && (node.Const is not null || !node.Enum.IsEmpty))
            {
                return JsonTypes.None;
            }

            if (node.Types != JsonTypes.None)
            {
                // Every integer is a number, so the two overlap.
                return (node.Types & (JsonTypes.Integer | JsonTypes.Number)) != JsonTypes.None
                    ? node.Types | JsonTypes.Integer | JsonTypes.Number
                    : node.Types;
            }

            if (node.Behavior.Target is not { } target)
            {
                return JsonTypes.None;
            }

            node = target();
        }

        return JsonTypes.None;
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

    /// <summary>Like <see cref="Unsupported"/>, for a keyword left out because a subschema isn't exact. Names the rule and where it is.</summary>
    private void UnsupportedBecause(string keyword, SchemaExactness.Cause? cause, string where, List<string> notes, string location)
    {
        notes.Add($"Also applies {keyword}, which {_profile.Name} can't state.");
        string because = cause is null ? string.Empty : $" The rule is {cause.Describe(where)}.";
        Unrepresentable(keyword, $"{keyword} is left out, because a looser rule inside it would make the input schema reject values that Zorg accepts.{because}", location);
    }
}
