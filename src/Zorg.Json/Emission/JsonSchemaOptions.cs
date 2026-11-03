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
    /// How schemas marked with <c>Named</c> are written. Default: <see cref="ReusedSchemaHandling.Inline"/>.
    /// Zod equivalent: the <c>reused</c> option.
    /// </summary>
    public ReusedSchemaHandling Reused { get; init; } = ReusedSchemaHandling.Inline;

    /// <summary>
    /// A prefix for references, such as <c>#/components/schemas/</c>. When set, definitions go in
    /// <see cref="EmitResult.Definitions"/> instead of the document. Default: <c>#/$defs/</c>.
    /// </summary>
    public string? ReferencePrefix { get; init; }
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
}

/// <summary>How schemas marked with <c>Named</c> are written.</summary>
public enum ReusedSchemaHandling
{
    /// <summary>Write the schema at each use.</summary>
    Inline,

    /// <summary>Write it once under <c>$defs</c> and refer to it with <c>$ref</c>.</summary>
    Ref,
}
