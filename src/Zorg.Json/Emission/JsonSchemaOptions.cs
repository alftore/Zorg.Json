using System;
using System.Text.Json.Nodes;

namespace Zorg.Json.Emission;

/// <summary>Options for <see cref="Schema{T}.ToJsonSchema"/>.</summary>
public sealed record JsonSchemaOptions
{
    /// <summary>The dialect to emit. Default: <see cref="JsonSchemaProfile.Draft202012"/>.</summary>
    public JsonSchemaProfile Target { get; init; } = JsonSchemaProfile.Draft202012;

    /// <summary>What the schema describes. Default: <see cref="SchemaDirection.Input"/>.</summary>
    public SchemaDirection Direction { get; init; } = SchemaDirection.Input;

    /// <summary>What to do with rules the target can't express. Default: <see cref="UnrepresentablePolicy.Annotate"/>.</summary>
    public UnrepresentablePolicy Unrepresentable { get; init; } = UnrepresentablePolicy.Annotate;

    /// <summary>
    /// Called for each rule that can't be stated, when <see cref="Unrepresentable"/> is <see cref="UnrepresentablePolicy.Callback"/>.
    /// It returns a fragment to merge into that subschema, or null to leave the rule out. Required for the
    /// <see cref="UnrepresentablePolicy.Callback"/> policy and ignored for the others.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A fragment must only state what is already true of the values Zorg accepts, such as a looser keyword
    /// or a note. A fragment that is stricter breaks the input guarantee, because the emitted schema would then reject
    /// what <c>SafeParse</c> accepts. Keywords that are already in the subschema are kept, so a fragment can't change them.
    /// </para>
    /// <para>
    /// Whether a subschema may appear under <c>not</c>, <c>if</c>, <c>maxContains</c> or <c>oneOf</c> is decided by the
    /// schema, not by what the handler returns: a node with a refinement stays inexact.
    /// </para>
    /// <para>
    /// The handler isn't called for diagnostics from a profile's rewriters and post-processors, which have no subschema to merge into.
    /// They are still reported.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// JsonSchemaOptions options = new()
    /// {
    ///     Unrepresentable = UnrepresentablePolicy.Callback,
    ///     UnrepresentableHandler = context => context.Keyword == "pattern"
    ///         ? new JsonObject { ["x-pattern-dotnet"] = context.Value?.DeepClone() }
    ///         : null,
    /// };
    /// </code>
    /// </example>
    public Func<UnrepresentableContext, JsonObject?>? UnrepresentableHandler { get; init; }

    /// <summary>
    /// How schemas marked with <c>Named</c> are written. Default: <see cref="ReusedSchemaHandling.Inline"/>.
    /// Zod equivalent: the <c>reused</c> option.
    /// </summary>
    public ReusedSchemaHandling Reused { get; init; } = ReusedSchemaHandling.Inline;

    /// <summary>
    /// A prefix for references, such as <c>#/components/schemas/</c>. When set, definitions go in
    /// <see cref="EmitResult.Definitions"/> instead of the document. Default: <c>#/$defs/</c>.
    /// </summary>
    public string? ReferencePrefix { get; init; }

    /// <summary>How many hex digits of its hash a definition that leaves the document has in its name. Tests force collisions with this, and 0 leaves names unhashed.</summary>
    internal int DefinitionTagLength { get; init; } = 16;
}

/// <summary>What an emitted schema describes. Zod equivalent: the <c>io</c> option of <c>z.toJSONSchema</c>.</summary>
public enum SchemaDirection
{
    /// <summary>The JSON that parsing accepts. Use it for request bodies, tool arguments and served schemas.</summary>
    Input,

    /// <summary>The value after parsing. Use it for responses and published events.</summary>
    Output,
}

/// <summary>What to do with rules the target can't express. Leaving a rule out only makes the schema looser.</summary>
public enum UnrepresentablePolicy
{
    /// <summary>Leave the rule out, note it in <c>description</c> and add a diagnostic.</summary>
    Annotate,

    /// <summary>Leave the rule out and add a diagnostic.</summary>
    Omit,

    /// <summary>Throw <see cref="System.InvalidOperationException"/>.</summary>
    Throw,

    /// <summary>
    /// Leave the rule out, add a diagnostic and ask <see cref="JsonSchemaOptions.UnrepresentableHandler"/> for a fragment to
    /// put in its place. Nothing is noted in <c>description</c>.
    /// </summary>
    Callback,
}

/// <summary>A rule that can't be stated, as given to <see cref="JsonSchemaOptions.UnrepresentableHandler"/>.</summary>
/// <param name="Keyword">The JSON Schema keyword left out, such as <c>pattern</c>. Null for a refinement.</param>
/// <param name="Message">What was left out and why. The same text as the diagnostic.</param>
/// <param name="Location">A JSON Pointer to the subschema in the emitted document. Empty for the root.</param>
/// <param name="Value">
/// The rule's value: the keyword's value for a keyword (the pattern text, the <c>minLength</c> number), or the
/// message of a refinement. Null when the rule has no value of its own.
/// </param>
/// <example>
/// <code>
/// JsonObject? Handle(UnrepresentableContext context) =>
///     context.Keyword is null ? new JsonObject { ["x-checked"] = true } : null;
/// </code>
/// </example>
public sealed record UnrepresentableContext(string? Keyword, string Message, string Location, JsonNode? Value);

/// <summary>How schemas marked with <c>Named</c> are written.</summary>
public enum ReusedSchemaHandling
{
    /// <summary>Write the schema at each use.</summary>
    Inline,

    /// <summary>Write it once under <c>$defs</c> and refer to it with <c>$ref</c>.</summary>
    Ref,
}
