using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
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
        Count("minContains", node => node.MinContains, node => node with { MinContains = null }),
        Count("maxContains", node => node.MaxContains, node => node with { MaxContains = null }),
        Count("minItems", node => node.MinItems, node => node with { MinItems = null }),
        Count("maxItems", node => node.MaxItems, node => node with { MaxItems = null }),
        Flag("uniqueItems", node => node.UniqueItems, node => node with { UniqueItems = null }),
        Named("patternProperties", node => node.PatternProperties, node => node with { PatternProperties = [] }),
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
    /// provider won't enforce. Removing <c>prefixItems</c> also removes a closing <c>items: false</c>. Zod equivalent: none.
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

    private static SchemaNode ClearPrefixItems(SchemaNode node) =>
        node with { PrefixItems = [], Items = node.Items?.BooleanValue == false ? null : node.Items };

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

        public SchemaNode Run() => Walk(root with { Defs = [] });

        private SchemaNode Walk(SchemaNode node)
        {
            if (node.Ref is null)
            {
                return node.MapChildren(Walk);
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

            SchemaNode expanded = Walk(target with { Defs = [] });
            _active.Remove(target);

            return expanded with
            {
                Description = node.Description ?? expanded.Description,
                Behavior = expanded.Behavior with { DefinitionName = null, Target = null, IsOptional = node.Behavior.IsOptional },
            };
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

            // The document root is now the wrapper, so a pointer into the original has to go through the property.
            ImmutableArray<KeyValuePair<string, JsonElement>> lifted = [.. node.Extensions.Where(extension => extension.Key == "definitions")];
            SchemaNode inner = Retarget(node with { Defs = [], Extensions = [.. node.Extensions.Except(lifted)] });

            return SchemaNode.True with
            {
                BooleanValue = null,
                Types = JsonTypes.Object,
                Properties = [new SchemaProperty(RootWrapperProperty, inner)],
                Required = [RootWrapperProperty],
                Defs = [.. node.Defs.Select(definition => definition with { Schema = Retarget(definition.Schema) })],
                Extensions = lifted,
                Behavior = SchemaBehavior.Empty with { UnknownProperties = UnknownProperties.Reject },
            };
        }

        private static SchemaNode Retarget(SchemaNode node)
        {
            SchemaNode mapped = node.MapChildren(Retarget);

            return mapped.Ref is { } reference && PointsIntoRoot(reference)
                ? mapped with { Ref = $"#/properties/{RootWrapperProperty}{reference["#".Length..]}" }
                : mapped;
        }

        private static bool PointsIntoRoot(string reference) =>
            reference == "#"
            || (reference.StartsWith("#/", StringComparison.Ordinal)
                && !reference.StartsWith("#/$defs/", StringComparison.Ordinal)
                && !reference.StartsWith("#/definitions/", StringComparison.Ordinal));
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
        public SchemaNode Rewrite(SchemaNode node, RewriteContext context)
        {
            List<string> moved = [];
            SchemaNode result = node;

            void Move(string keyword, string text)
            {
                moved.Add($"{keyword} {text}");
                context.Report(new EmitDiagnostic(keyword, $"{context.Options.Target.Name} can't state {keyword}, so it moved to description."));
            }

            foreach (Relocation relocation in Relocations.Where(relocation => keywords.Contains(relocation.Keyword)))
            {
                if (relocation.Read(node) is { } text)
                {
                    Move(relocation.Keyword, text);
                    result = relocation.Clear(result);
                }
            }

            foreach ((string keyword, JsonElement value) in node.Extensions.Where(extension => keywords.Contains(extension.Key)))
            {
                Move(keyword, value.GetRawText());
            }

            if (moved.Count == 0)
            {
                return node;
            }

            result = result with { Extensions = [.. result.Extensions.Where(extension => !keywords.Contains(extension.Key))] };
            return Annotate(result, $"Must also satisfy: {string.Join("; ", moved)}.", context);
        }
    }
}
