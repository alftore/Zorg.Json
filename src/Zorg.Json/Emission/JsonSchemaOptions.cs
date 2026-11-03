namespace Zorg.Json.Emission;

/// <summary>Options for <see cref="Schema{T}.ToJsonSchema"/>.</summary>
public sealed record JsonSchemaOptions
{
    /// <summary>The dialect to emit. Default: <see cref="JsonSchemaProfile.Draft202012"/>.</summary>
    public JsonSchemaProfile Target { get; init; } = JsonSchemaProfile.Draft202012;

    /// <summary>What the schema describes. Default: <see cref="SchemaDirection.Input"/>.</summary>
    public SchemaDirection Direction { get; init; } = SchemaDirection.Input;

    /// <summary>What to do with rules JSON Schema can't express. Default: <see cref="UnrepresentablePolicy.Annotate"/>.</summary>
    public UnrepresentablePolicy Unrepresentable { get; init; } = UnrepresentablePolicy.Annotate;
}

/// <summary>What an emitted schema describes. Zod equivalent: the <c>io</c> option of <c>z.toJSONSchema</c>.</summary>
public enum SchemaDirection
{
    /// <summary>The JSON that parsing accepts. Use it for request bodies, tool arguments and served schemas.</summary>
    Input,

    /// <summary>The value after parsing. Use it for responses and published events.</summary>
    Output,
}

/// <summary>What to do with rules JSON Schema can't express. Leaving a rule out only makes the schema looser.</summary>
public enum UnrepresentablePolicy
{
    /// <summary>Leave the rule out, note it in <c>description</c> and add a diagnostic.</summary>
    Annotate,

    /// <summary>Leave the rule out and add a diagnostic.</summary>
    Omit,

    /// <summary>Throw <see cref="System.InvalidOperationException"/>.</summary>
    Throw,
}
