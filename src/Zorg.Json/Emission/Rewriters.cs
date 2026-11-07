using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using Zorg.Json.Model;

namespace Zorg.Json.Emission;

/// <summary>
/// Provider-neutral building blocks for a <see cref="JsonSchemaProfile"/>. They only change what's emitted: parsing
/// always uses the full schema. Diagnostics from a rewriter have an empty <see cref="EmitDiagnostic.Location"/>,
/// because rewriters don't see their position in the document.
/// </summary>
/// <example>
/// <code>
/// JsonSchemaProfile closed = JsonSchemaProfile.Draft202012.With(Rewriters.CloseAllObjects, Rewriters.RequireAllAsNullable);
/// JsonObject schema = Z.Object().Passthrough().ToJsonSchema(new() { Target = closed });
/// </code>
/// </example>
public static class Rewriters
{
    /// <summary>The property that holds the original schema after <see cref="RequireObjectRoot"/> wraps it.</summary>
    public const string RootWrapperProperty = "data";

    private static readonly JsonSerializerOptions TextOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonSchemaProfile BareProfile = new(string.Empty, JsonSchemaDialect.Draft202012, [], []);

    private static readonly ImmutableArray<Relocation> Relocations =
    [
        Text("title", node => node.Title, node => node with { Title = null }),
        Flag("deprecated", node => node.Deprecated, node => node with { Deprecated = null }),
        Flag("readOnly", node => node.ReadOnly, node => node with { ReadOnly = null }),
        Flag("writeOnly", node => node.WriteOnly, node => node with { WriteOnly = null }),
        Values("examples", node => node.Examples, node => node with { Examples = [] }),
        Value("const", node => node.Const, node => node with { Const = null }),
        Values("enum", node => node.Enum, node => node with { Enum = [] }),
        Value("minimum", node => node.Minimum, node => node with { Minimum = null }),
        Value("maximum", node => node.Maximum, node => node with { Maximum = null }),
        Value("exclusiveMinimum", node => node.ExclusiveMinimum, node => node with { ExclusiveMinimum = null }),
        Value("exclusiveMaximum", node => node.ExclusiveMaximum, node => node with { ExclusiveMaximum = null }),
        Value("multipleOf", node => node.MultipleOf, node => node with { MultipleOf = null }),
        Count("minLength", node => node.MinLength, node => node with { MinLength = null }),
        Count("maxLength", node => node.MaxLength, node => node with { MaxLength = null }),
        Text("pattern", node => node.Pattern, node => node with { Pattern = null }),
        Text("format", node => node.Format, node => node with { Format = null }),
        Text("contentEncoding", node => node.ContentEncoding, node => node with { ContentEncoding = null }),
        Text("contentMediaType", node => node.ContentMediaType, node => node with { ContentMediaType = null }),
        Single("contentSchema", node => node.ContentSchema, node => node with { ContentSchema = null }),
        List("prefixItems", node => node.PrefixItems, ClearPrefixItems),
        Single("contains", node => node.Contains, node => node with { Contains = null }),
        Count("minContains", node => node.MinContains, node => node with { MinContains = null, Contains = node.MinContains == 0 ? null : node.Contains }),
        Count("maxContains", node => node.MaxContains, node => node with { MaxContains = null }),
        Count("minItems", node => node.MinItems, node => node with { MinItems = null }),
        Count("maxItems", node => node.MaxItems, node => node with { MaxItems = null }),
        Flag("uniqueItems", node => node.UniqueItems, node => node with { UniqueItems = null }),
        Named("patternProperties", node => node.PatternProperties, ClearPatternProperties),
        Single("propertyNames", node => node.PropertyNames, node => node with { PropertyNames = null }),
        new("dependentRequired", DependentRequiredText, node => node with { DependentRequired = [] }),
        Named("dependentSchemas", node => node.DependentSchemas, node => node with { DependentSchemas = [] }),
        Count("minProperties", node => node.MinProperties, node => node with { MinProperties = null }),
        Count("maxProperties", node => node.MaxProperties, node => node with { MaxProperties = null }),
        List("allOf", node => node.AllOf, node => node with { AllOf = [] }),
        List("anyOf", node => node.AnyOf, node => node with { AnyOf = [] }),
        List("oneOf", node => node.OneOf, node => node with { OneOf = [] }),
        Single("not", node => node.Not, node => node with { Not = null }),
        Single("if", node => node.If, node => node with { If = null }),
        Single("then", node => node.Then, node => node with { Then = null }),
        Single("else", node => node.Else, node => node with { Else = null }),
        Single("unevaluatedItems", node => node.UnevaluatedItems, node => node with { UnevaluatedItems = null }),
        Single("unevaluatedProperties", node => node.UnevaluatedProperties, node => node with { UnevaluatedProperties = null }),
    ];

    /// <summary>
    /// Sets <c>additionalProperties: false</c> on every object. A schema-valued <c>additionalProperties</c> goes to
    /// <c>description</c> with a diagnostic. Closing an open object makes the emitted schema stricter than parsing, which
    /// suits a schema that steers a model. Zod equivalent: <c>z.strictObject()</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// JsonSchemaProfile closed = JsonSchemaProfile.Draft202012.With(Rewriters.CloseAllObjects);
    /// </code>
    /// </example>
    public static ISchemaRewriter CloseAllObjects { get; } = new CloseObjectsRewriter();

    /// <summary>
    /// Lists every property in <c>required</c> and adds <c>null</c> to the type of those that were optional, because a
    /// strict provider can't leave a key out. Zod equivalent: replacing <c>.optional()</c> with <c>.nullable()</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// JsonSchemaProfile strict = JsonSchemaProfile.Draft202012.With(Rewriters.RequireAllAsNullable);
    /// </code>
    /// </example>
    public static ISchemaRewriter RequireAllAsNullable { get; } = new RequireAllRewriter();

    /// <summary>
    /// Replaces each <c>$ref</c> with its target and removes <c>$defs</c>. A reference back into its own expansion is
    /// rejected: it becomes the empty schema <c>{}</c>, which is only looser, with a <c>$ref</c> diagnostic, so the
    /// <see cref="UnrepresentablePolicy.Throw"/> policy throws. List it first, so that later rewriters see the inlined nodes.
    /// Zod equivalent: the <c>cycles</c> option of <c>z.toJSONSchema</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// JsonSchemaProfile flat = JsonSchemaProfile.Draft202012.With(Rewriters.InlineOrRejectRecursion);
    /// </code>
    /// </example>
    public static ISchemaRewriter InlineOrRejectRecursion { get; } = new InlineRecursionRewriter();

    /// <summary>
    /// Wraps a root that isn't <c>type: object</c> in a closed object with one required property, <see cref="RootWrapperProperty"/>.
    /// Providers and MCP's <c>inputSchema</c> need an object root. The model's answer then arrives wrapped.
    /// </summary>
    /// <example>
    /// <code>
    /// JsonSchemaProfile objectRoot = JsonSchemaProfile.Draft202012.With(Rewriters.RequireObjectRoot);
    /// JsonObject schema = Z.Array(Z.String()).ToJsonSchema(new() { Target = objectRoot });
    /// </code>
    /// </example>
    public static ISchemaRewriter RequireObjectRoot { get; } = new ObjectRootRewriter();

    /// <summary>
    /// Replaces the boolean schema <c>true</c> with <c>{}</c> and <c>false</c> with <c>{ "not": {} }</c>. A boolean
    /// <c>additionalProperties</c> stays as it is. Zod equivalent: none.
    /// </summary>
    /// <example>
    /// <code>
    /// JsonSchemaProfile profile = JsonSchemaProfile.Draft202012.With(Rewriters.ConvertBooleanSchemas);
    /// </code>
    /// </example>
    public static ISchemaRewriter ConvertBooleanSchemas { get; } = new BooleanSchemaRewriter();

    /// <summary>Moves the <c>default</c> keyword into <c>description</c>, with a diagnostic. Zod equivalent: none.</summary>
    /// <example>
    /// <code>
    /// JsonSchemaProfile profile = JsonSchemaProfile.Draft202012.With(Rewriters.MoveDefaultToDescription);
    /// </code>
    /// </example>
    public static ISchemaRewriter MoveDefaultToDescription { get; } = new MoveDefaultRewriter();

    /// <summary>
    /// Removes keywords and states them in <c>description</c>, each with a diagnostic, so that a model still sees rules a
    /// provider won't enforce. Removing <c>prefixItems</c> also removes a closing <c>items: false</c>. A keyword under <c>not</c>, in the
    /// condition of <c>if</c>, in a branch of <c>oneOf</c> or in <c>contains</c> beside <c>maxContains</c> would make the schema stricter
    /// if it were removed, so that applicator moves to <c>description</c> as a whole. Zod equivalent: none.
    /// </summary>
    /// <param name="keywords">The keywords to move. <c>$ref</c>, <c>properties</c> and other structure can't be moved.</param>
    /// <returns>The rewriter.</returns>
    /// <example>
    /// <code>
    /// JsonSchemaProfile profile = JsonSchemaProfile.Draft202012.With(Rewriters.RelocateToDescription("minLength", "maxLength"));
    /// </code>
    /// </example>
    public static ISchemaRewriter RelocateToDescription(params ReadOnlySpan<string> keywords) =>
        new RelocateRewriter(ImmutableHashSet.Create(StringComparer.Ordinal, keywords));

    /// <summary>Tells whether <see cref="RequireObjectRoot"/> leaves a root as it is.</summary>
    /// <param name="node">The root node.</param>
    /// <returns>Whether the node is exactly <c>type: object</c>, with no <c>$ref</c>, <c>anyOf</c> or <c>oneOf</c>.</returns>
    /// <example>
    /// <code>
    /// bool wrapped = !Rewriters.HasObjectRoot(Z.Array(Z.String()).ToNode());
    /// </code>
    /// </example>
    public static bool HasObjectRoot(SchemaNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.Types == JsonTypes.Object && node.Ref is null && node.AnyOf.IsEmpty && node.OneOf.IsEmpty;
    }

    /// <summary>Whether a profile wraps this root. Inlining first replaces a root reference with its target, which the wrapper then sees.</summary>
    internal static bool WrapsRoot(SchemaNode root, ImmutableArray<ISchemaRewriter> rewriters)
    {
        int wrapping = rewriters.IndexOf(RequireObjectRoot);

        if (wrapping < 0)
        {
            return false;
        }

        int inlining = rewriters.IndexOf(InlineOrRejectRecursion);
        SchemaNode seen = root;

        for (int hop = 0; inlining >= 0 && inlining < wrapping && hop < 32 && seen.Ref is not null && seen.Behavior.Target is { } target; hop++)
        {
            seen = target();
        }

        return !HasObjectRoot(seen);
    }

    /// <summary>Tells whether a node accepts JSON <c>null</c>, looking only at its type keywords, unions and resolved references.</summary>
    /// <param name="node">The node.</param>
    /// <returns>Whether <c>null</c> passes. False for a <c>$ref</c> that can't be resolved.</returns>
    /// <example>
    /// <code>
    /// bool accepted = Rewriters.AcceptsNull(Z.String().Nullable().ToNode());
    /// </code>
    /// </example>
    public static bool AcceptsNull(SchemaNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return AcceptsNull(node, depth: 0);
    }

    private static bool AcceptsNull(SchemaNode node, int depth)
    {
        const int MaxDepth = 32;

        if (node.BooleanValue is { } allowed)
        {
            return allowed;
        }

        if (depth > MaxDepth)
        {
            return false;
        }

        if (node.Ref is not null)
        {
            return node.Behavior.Target is { } target && AcceptsNull(target(), depth + 1);
        }

        if (node.Const is { } constant)
        {
            return constant.ValueKind == JsonValueKind.Null;
        }

        if (!node.Enum.IsEmpty)
        {
            return node.Enum.Any(value => value.ValueKind == JsonValueKind.Null);
        }

        if (node.Types != JsonTypes.None && !node.Types.HasFlag(JsonTypes.Null))
        {
            return false;
        }

        return (node.AnyOf.IsEmpty || node.AnyOf.Any(branch => AcceptsNull(branch, depth + 1)))
            && (node.OneOf.IsEmpty || node.OneOf.Any(branch => AcceptsNull(branch, depth + 1)))
            && node.AllOf.All(branch => AcceptsNull(branch, depth + 1));
    }

    private static SchemaNode Annotate(SchemaNode node, string note, RewriteContext context)
    {
        if (context.Options.Unrepresentable != UnrepresentablePolicy.Annotate)
        {
            return node;
        }

        return node with { Description = node.Description is null ? note : $"{node.Description} {note}" };
    }

    private static string SchemaText(SchemaNode node) =>
        JsonSchemaEmitter.Emit(node, new JsonSchemaOptions { Target = BareProfile, Unrepresentable = UnrepresentablePolicy.Omit })
            .Schema.ToJsonString(TextOptions);

    private static string ListText(IEnumerable<string> items) => $"[{string.Join(", ", items)}]";

    private static string? DependentRequiredText(SchemaNode node) => node.DependentRequired.IsEmpty
        ? null
        : string.Join(", ", node.DependentRequired.Select(rule => $"{rule.Key} needs {ListText(rule.Value)}"));

    /// <summary>Without <c>prefixItems</c>, <c>items</c> would also apply to the items those covered.</summary>
    private static SchemaNode ClearPrefixItems(SchemaNode node) => node with { PrefixItems = [], Items = null };

    /// <summary>Without <c>patternProperties</c>, what closes the object would also apply to the properties those covered.</summary>
    private static SchemaNode ClearPatternProperties(SchemaNode node) => node with
    {
        PatternProperties = [],
        AdditionalProperties = null,
        Behavior = node.Behavior.UnknownProperties == UnknownProperties.Reject ? node.Behavior with { UnknownProperties = UnknownProperties.Allow } : node.Behavior,
    };

    private static Relocation Text(string keyword, Func<SchemaNode, string?> read, Func<SchemaNode, SchemaNode> clear) => new(keyword, read, clear);

    private static Relocation Count(string keyword, Func<SchemaNode, int?> read, Func<SchemaNode, SchemaNode> clear) =>
        new(keyword, node => read(node)?.ToString(CultureInfo.InvariantCulture), clear);

    private static Relocation Flag(string keyword, Func<SchemaNode, bool?> read, Func<SchemaNode, SchemaNode> clear) =>
        new(keyword, node => read(node) is { } value ? (value ? "true" : "false") : null, clear);

    private static Relocation Value(string keyword, Func<SchemaNode, JsonElement?> read, Func<SchemaNode, SchemaNode> clear) =>
        new(keyword, node => read(node)?.GetRawText(), clear);

    private static Relocation Values(string keyword, Func<SchemaNode, ImmutableArray<JsonElement>> read, Func<SchemaNode, SchemaNode> clear) =>
        new(keyword, node => read(node) is { IsEmpty: false } values ? ListText(values.Select(value => value.GetRawText())) : null, clear);

    private static Relocation Single(string keyword, Func<SchemaNode, SchemaNode?> read, Func<SchemaNode, SchemaNode> clear) =>
        new(keyword, node => read(node) is { } value ? SchemaText(value) : null, clear);

    private static Relocation List(string keyword, Func<SchemaNode, ImmutableArray<SchemaNode>> read, Func<SchemaNode, SchemaNode> clear) =>
        new(keyword, node => read(node) is { IsEmpty: false } values ? ListText(values.Select(SchemaText)) : null, clear);

    private static Relocation Named(string keyword, Func<SchemaNode, ImmutableArray<SchemaProperty>> read, Func<SchemaNode, SchemaNode> clear) =>
        new(keyword, node => read(node) is { IsEmpty: false } values ? ListText(values.Select(value => $"{value.Name}: {SchemaText(value.Schema)}")) : null, clear);

    private sealed record Relocation(string Keyword, Func<SchemaNode, string?> Read, Func<SchemaNode, SchemaNode> Clear);

    private sealed class CloseObjectsRewriter : ISchemaRewriter
    {
        public SchemaNode Rewrite(SchemaNode node, RewriteContext context)
        {
            node = Flatten(node, context);

            if (!node.Types.HasFlag(JsonTypes.Object) && node.Properties.IsEmpty)
            {
                return node;
            }

            SchemaNode closed = node with
            {
                AdditionalProperties = null,
                Behavior = node.Behavior with { UnknownProperties = UnknownProperties.Reject },
            };

            if (node.AdditionalProperties is null or { BooleanValue: not null })
            {
                return closed;
            }

            context.Report(new EmitDiagnostic("additionalProperties", $"{context.Options.Target.Name} needs closed objects, so additionalProperties can't be stated."));
            return Annotate(closed, $"Must also satisfy: additionalProperties {SchemaText(node.AdditionalProperties)}.", context);
        }

        /// <summary>Closed objects in an <c>allOf</c> reject each other's properties, so objects that only list properties become one object.</summary>
        private static SchemaNode Flatten(SchemaNode node, RewriteContext context)
        {
            bool nodeIsObject = !node.Properties.IsEmpty || node.Types.HasFlag(JsonTypes.Object);

            // A union of objects closes its objects too, so it rejects the properties of the others as well.
            if (node.AllOf.Count(branch => IsObject(branch) || branch.AnyOf.Any(IsObject) || branch.OneOf.Any(IsObject)) + (nodeIsObject ? 1 : 0) < 2)
            {
                return node;
            }

            Dictionary<string, SchemaProperty> properties = new(StringComparer.Ordinal);
            bool canFlatten = node.AllOf.All(IsPlainObject);

            foreach (SchemaProperty property in node.AllOf.SelectMany(branch => branch.Properties).Concat(node.Properties))
            {
                canFlatten &= !properties.TryGetValue(property.Name, out SchemaProperty? other) || other == property;
                properties[property.Name] = property;
            }

            if (!canFlatten)
            {
                context.Report(new EmitDiagnostic("allOf", $"{context.Options.Target.Name} needs closed objects, and closed objects in an allOf reject each other's properties, so no value can pass. Use Extend."));
                return node;
            }

            return node with
            {
                Types = JsonTypes.Object,
                AllOf = [],
                Properties = [.. properties.Values],
                Required = [.. node.Required.Concat(node.AllOf.SelectMany(branch => branch.Required)).Distinct(StringComparer.Ordinal)],
            };
        }

        private static bool IsObject(SchemaNode node) => !node.Properties.IsEmpty || node.Types.HasFlag(JsonTypes.Object);

        private static bool IsPlainObject(SchemaNode node) =>
            node.Types == JsonTypes.Object
            && node is { Ref: null, DynamicRef: null, Not: null, If: null, AdditionalProperties: null, PropertyNames: null, MinProperties: null, MaxProperties: null, UnevaluatedProperties: null }
            && node is { Title: null, Description: null, Default: null, Const: null }
            && node.Enum.IsEmpty && node.Behavior.Refinements.IsEmpty && node.Behavior.Normalizers.IsEmpty
            && node.AllOf.IsEmpty && node.AnyOf.IsEmpty && node.OneOf.IsEmpty && node.PatternProperties.IsEmpty && node.DependentRequired.IsEmpty && node.DependentSchemas.IsEmpty;
    }

    private sealed class RequireAllRewriter : ISchemaRewriter
    {
        public SchemaNode Rewrite(SchemaNode node, RewriteContext context)
        {
            if (node.Properties.IsEmpty)
            {
                return node;
            }

            bool isInput = context.Options.Direction == SchemaDirection.Input;
            ImmutableArray<SchemaProperty>.Builder properties = ImmutableArray.CreateBuilder<SchemaProperty>(node.Properties.Length);
            List<string> required = [];

            foreach (SchemaProperty property in node.Properties)
            {
                if (isInput && property.OutputOnly)
                {
                    properties.Add(property);
                    continue;
                }

                required.Add(property.Name);
                properties.Add(node.Required.Contains(property.Name) ? property : property with { Schema = Nullable(property.Schema) });
            }

            return node with
            {
                Properties = properties.MoveToImmutable(),
                Required = [.. required, .. node.Required.Except(required, StringComparer.Ordinal)],
            };
        }

        private static SchemaNode Nullable(SchemaNode schema)
        {
            SchemaNode required = schema with { Behavior = schema.Behavior with { IsOptional = false, FillDefault = false } };

            if (AcceptsNull(required))
            {
                return required;
            }

            // Adding null to a named node would change it at every other use.
            return required.Behavior.DefinitionName is null
                ? required.WithNull()
                : SchemaNode.True with { BooleanValue = null, AnyOf = [required, SchemaNode.True with { BooleanValue = null, Types = JsonTypes.Null }] };
        }
    }

    private sealed class InlineRecursionRewriter : ISchemaRewriter
    {
        public SchemaNode Rewrite(SchemaNode node, RewriteContext context) =>
            context.IsRoot ? new Inliner(node, context).Run() : node;
    }

    private sealed class Inliner(SchemaNode root, RewriteContext context)
    {
        // Inlining repeats a definition at every use, so definitions that each use the next twice double at every level.
        private const int MaxExpansions = 5_000;

        private const int MaxNesting = 200;

        private readonly HashSet<SchemaNode> _active = new(ReferenceEqualityComparer.Instance) { root };

        private int _expansions;
        private int _rejected;

        public SchemaNode Run() => Walk(root with { Defs = [] });

        private SchemaNode Walk(SchemaNode node)
        {
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();

            if (node.Ref is null)
            {
                return WalkChildren(node);
            }

            SchemaNode? target = Resolve(node);

            if (target is null)
            {
                context.Report(new EmitDiagnostic("$ref", $"The reference {node.Ref} can't be inlined."));
                return node;
            }

            if (!_active.Add(target))
            {
                return Reject(node, tooLarge: false);
            }

            if (++_expansions > MaxExpansions || _active.Count > MaxNesting)
            {
                _active.Remove(target);
                return Reject(node, tooLarge: true);
            }

            int rejectedBefore = _rejected;
            SchemaNode expanded = Walk(target with { Defs = [] });
            bool lostInTarget = _rejected != rejectedBefore;
            _active.Remove(target);
            SchemaNode beside = WalkChildren(node with
            {
                Ref = null,
                Defs = [],
                Description = null,
                Behavior = node.Behavior with { Target = null, DefinitionName = null, IsOptional = false, FillDefault = false },
            });

            // What the reference evaluated went with it, so unevaluated* beside it can't stay.
            if (lostInTarget)
            {
                beside = beside with { UnevaluatedItems = null, UnevaluatedProperties = null };
            }

            SchemaNode inlined = expanded with
            {
                Description = node.Description ?? expanded.Description,
                Behavior = expanded.Behavior with { DefinitionName = null, Target = null, IsOptional = node.Behavior.IsOptional },
            };

            // What sits beside a $ref applies as well, so both have to stay.
            bool isPlain = (beside with { Behavior = SchemaBehavior.Empty }) == (SchemaNode.True with { BooleanValue = null, Behavior = SchemaBehavior.Empty })
                && beside.Behavior.Refinements.IsEmpty && beside.Behavior.Normalizers.IsEmpty;

            return isPlain
                ? inlined
                : beside with
                {
                    AllOf = [.. beside.AllOf, inlined with { Description = null, Behavior = inlined.Behavior with { IsOptional = false } }],
                    Description = node.Description,
                    Behavior = beside.Behavior with { IsOptional = node.Behavior.IsOptional },
                };
        }

        /// <summary>
        /// A reference that can't be inlined becomes an open schema, which is looser. Where a looser subschema makes the document
        /// stricter, the keyword goes with it. An open schema evaluates nothing, so <c>unevaluated*</c> goes too.
        /// </summary>
        private SchemaNode WalkChildren(SchemaNode node)
        {
            int start = _rejected;
            HashSet<SchemaNode> tainted = new(ReferenceEqualityComparer.Instance);

            SchemaNode mapped = node.MapChildren((child, negative) =>
            {
                int before = _rejected;
                SchemaNode walked = Walk(child);

                if (negative && _rejected != before)
                {
                    tainted.Add(walked);
                }

                return walked;
            });

            if (_rejected == start)
            {
                return mapped;
            }

            void Drop(string keyword) =>
                context.Report(new EmitDiagnostic(keyword, $"{context.Options.Target.Name} can't state {keyword}, because a reference inside it can't be inlined."));

            if (mapped.Not is { } negated && tainted.Contains(negated))
            {
                Drop("not");
                mapped = mapped with { Not = null };
            }

            if (mapped.If is { } condition && tainted.Contains(condition))
            {
                Drop("if");
                mapped = mapped with { If = null, Then = null, Else = null };
            }

            if (mapped.OneOf.Any(tainted.Contains))
            {
                Drop("oneOf");
                mapped = mapped with { OneOf = [] };
            }

            if (mapped.Contains is { } contained && tainted.Contains(contained))
            {
                Drop("contains");
                mapped = mapped with { Contains = null, MinContains = null, MaxContains = null };
            }

            if (mapped.UnevaluatedProperties is not null)
            {
                Drop("unevaluatedProperties");
                mapped = mapped with { UnevaluatedProperties = null };
            }

            if (mapped.UnevaluatedItems is not null)
            {
                Drop("unevaluatedItems");
                mapped = mapped with { UnevaluatedItems = null };
            }

            return mapped;
        }

        private SchemaNode? Resolve(SchemaNode node)
        {
            if (node.Behavior.Target is { } target)
            {
                return target();
            }

            const string DefsPrefix = "#/$defs/";

            if (node.Ref == "#")
            {
                return root;
            }

            return node.Ref!.StartsWith(DefsPrefix, StringComparison.Ordinal)
                ? root.Defs.FirstOrDefault(definition => definition.Name == Unescape(node.Ref[DefsPrefix.Length..]))?.Schema
                : null;
        }

        private static string Unescape(string name) =>
            name.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

        private SchemaNode Reject(SchemaNode node, bool tooLarge)
        {
            _rejected++;
            context.Report(new EmitDiagnostic(
                "$ref",
                tooLarge
                    ? $"{context.Options.Target.Name} can't state the reference {node.Ref}, because inlining it makes the schema too large."
                    : $"{context.Options.Target.Name} can't state the recursive reference {node.Ref}."));

            SchemaNode open = SchemaNode.True with
            {
                BooleanValue = null,
                Description = node.Description,
                Behavior = node.Behavior with { DefinitionName = null, Target = null },
            };

            return Annotate(open, $"{(tooLarge ? "Not inlined" : "Recursive")}: must also satisfy {node.Ref}.", context);
        }
    }

    private sealed class ObjectRootRewriter : ISchemaRewriter
    {
        public SchemaNode Rewrite(SchemaNode node, RewriteContext context)
        {
            if (!context.IsRoot || HasObjectRoot(node))
            {
                return node;
            }

            return SchemaNode.True with
            {
                BooleanValue = null,
                Types = JsonTypes.Object,
                Properties = [new SchemaProperty(RootWrapperProperty, node)],
                Required = [RootWrapperProperty],
                Behavior = SchemaBehavior.Empty with { UnknownProperties = UnknownProperties.Reject },
            };
        }
    }

    private sealed class BooleanSchemaRewriter : ISchemaRewriter
    {
        public SchemaNode Rewrite(SchemaNode node, RewriteContext context)
        {
            SchemaNode mapped = node.MapChildren(Convert) with { AdditionalProperties = node.AdditionalProperties };
            return context.IsRoot ? Convert(mapped) : mapped;
        }

        private static SchemaNode Convert(SchemaNode node) => node.BooleanValue switch
        {
            true => node with { BooleanValue = null },
            false => node with { BooleanValue = null, Not = SchemaNode.True with { BooleanValue = null } },
            _ => node,
        };
    }

    private sealed class MoveDefaultRewriter : ISchemaRewriter
    {
        public SchemaNode Rewrite(SchemaNode node, RewriteContext context)
        {
            if (node.Default is not { } value)
            {
                return node;
            }

            context.Report(new EmitDiagnostic("default", $"{context.Options.Target.Name} can't state default."));
            return Annotate(node with { Default = null }, $"Default: {value.GetRawText()}.", context);
        }
    }

    private sealed class RelocateRewriter(ImmutableHashSet<string> keywords) : ISchemaRewriter
    {
        private static readonly ImmutableHashSet<string> Evaluating =
        [
            "properties", "patternProperties", "additionalProperties", "prefixItems", "items", "contains", "minContains",
            "allOf", "anyOf", "oneOf", "if", "then", "else", "dependentSchemas",
        ];

        /// <summary>
        /// True when what evaluates may move away. Subschemas are rewritten before the schema around them, so what moved from a
        /// subschema can't be seen here, and anything that applies to the same value counts as lost. That can only make the result looser.
        /// </summary>
        private bool LosesEvaluations(SchemaNode node) =>
            !node.AllOf.IsEmpty || !node.AnyOf.IsEmpty || !node.OneOf.IsEmpty || node.If is not null || !node.DependentSchemas.IsEmpty
            || node.Behavior.Target is not null || (node.Contains is not null && node.MaxContains is not null)
            || Relocations.Any(relocation => keywords.Contains(relocation.Keyword) && Evaluating.Contains(relocation.Keyword) && relocation.Read(node) is not null);

        public SchemaNode Rewrite(SchemaNode node, RewriteContext context)
        {
            // Where a looser subschema makes the schema stricter, the keyword stays, and the schema around it decides what to do.
            if (context.InNegativePosition)
            {
                return node;
            }

            List<string> moved = [];
            SchemaNode result = node;

            void Move(string keyword, string text)
            {
                moved.Add($"{keyword} {text}");
                context.Report(new EmitDiagnostic(keyword, $"{context.Options.Target.Name} can't state {keyword}, so it moved to description."));
            }

            // Taking a rule out of not, the condition of if, or a branch of oneOf would change which documents pass the
            // applicator, so the whole applicator is stated in the description instead.
            if (node.Not is { } negated && Holds(negated))
            {
                Move("not", SchemaText(negated));
                result = result with { Not = null };
            }

            if (node.If is { } condition && Holds(condition))
            {
                Move("if", SchemaText(condition));

                foreach ((string keyword, SchemaNode? branch) in new[] { ("then", node.Then), ("else", node.Else) })
                {
                    if (branch is not null)
                    {
                        Move(keyword, SchemaText(branch));
                    }
                }

                result = result with { If = null, Then = null, Else = null };
            }

            if (node.OneOf.Length > 1 && node.OneOf.Any(Holds))
            {
                Move("oneOf", ListText(node.OneOf.Select(SchemaText)));
                result = result with { OneOf = [] };
            }

            if (node.Contains is { } contained && node.MaxContains is not null && Holds(contained))
            {
                Move("contains", SchemaText(contained));
                result = result with { Contains = null, MinContains = null, MaxContains = null };
            }

            foreach (Relocation relocation in Relocations.Where(relocation => keywords.Contains(relocation.Keyword)))
            {
                if (relocation.Read(result) is { } text)
                {
                    Move(relocation.Keyword, text);
                    result = relocation.Clear(result);
                }
            }

            foreach ((string keyword, JsonElement value) in node.Extensions.Where(extension => keywords.Contains(extension.Key)))
            {
                Move(keyword, value.GetRawText());
            }

            // What goes no longer evaluates, here or in anything that applies to the same value, so what depends on it would
            // reject what it allowed. And a looser subschema under these keywords makes them stricter.
            bool lostEvaluations = (result.UnevaluatedProperties is not null || result.UnevaluatedItems is not null) && LosesEvaluations(node);

            if (result.UnevaluatedProperties is { } properties && (lostEvaluations || Holds(properties)))
            {
                Move("unevaluatedProperties", SchemaText(properties));
                result = result with { UnevaluatedProperties = null };
            }

            if (result.UnevaluatedItems is { } items && (lostEvaluations || Holds(items)))
            {
                Move("unevaluatedItems", SchemaText(items));
                result = result with { UnevaluatedItems = null };
            }

            if (moved.Count == 0)
            {
                return node;
            }

            result = result with { Extensions = [.. result.Extensions.Where(extension => !keywords.Contains(extension.Key))] };
            return Annotate(result, $"Must also satisfy: {string.Join("; ", moved)}.", context);
        }

        /// <summary>Whether a keyword this rewriter moves is anywhere in the subschema, which is then left as it is.</summary>
        private bool Holds(SchemaNode subschema)
        {
            if (_held.TryGetValue(subschema, out object? known))
            {
                return (bool)known;
            }

            bool result = Walk(subschema, out HashSet<SchemaNode> walked);

            // A walk that found nothing has judged everything it reached; one that found something has judged only where it began.
            if (result)
            {
                _held.TryAdd(subschema, true);
            }
            else
            {
                foreach (SchemaNode node in walked)
                {
                    _held.TryAdd(node, false);
                }
            }

            return result;
        }

        private readonly ConditionalWeakTable<SchemaNode, object> _held = new();

        private bool Walk(SchemaNode subschema, out HashSet<SchemaNode> seen)
        {
            seen = new(ReferenceEqualityComparer.Instance);
            Stack<SchemaNode> pending = new([subschema]);

            while (pending.TryPop(out SchemaNode? next))
            {
                if (!seen.Add(next))
                {
                    continue;
                }

                if (seen.Count > SchemaExactness.MaxWalked)
                {
                    throw SchemaExactness.TooLarge();
                }

                if (_held.TryGetValue(next, out object? judged))
                {
                    if ((bool)judged)
                    {
                        return true;
                    }

                    continue;
                }

                // A definition that loses unevaluated* gets looser like one that loses any other keyword.
                if (Relocations.Any(relocation => keywords.Contains(relocation.Keyword) && relocation.Read(next) is not null)
                    || next.Extensions.Any(extension => keywords.Contains(extension.Key))
                    || ((next.UnevaluatedProperties is not null || next.UnevaluatedItems is not null) && LosesEvaluations(next)))
                {
                    return true;
                }

                foreach (SchemaNode child in next.Children)
                {
                    pending.Push(child);
                }

                // A reference is written as a definition that is loosened like any other, wherever it is used, so what it names counts too.
                if (next.Behavior.Target is { } target)
                {
                    pending.Push(target());
                }
            }

            return false;
        }
    }
}
