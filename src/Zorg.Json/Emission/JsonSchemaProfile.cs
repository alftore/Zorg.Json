namespace Zorg.Json.Emission;

/// <summary>A JSON Schema dialect to emit. Zod equivalent: the <c>target</c> option of <c>z.toJSONSchema</c>.</summary>
public sealed class JsonSchemaProfile
{
    private JsonSchemaProfile(string name, string dialect)
    {
        Name = name;
        Dialect = dialect;
    }

    /// <summary>JSON Schema Draft 2020-12, the default.</summary>
    public static JsonSchemaProfile Draft202012 { get; } = new("draft-2020-12", "https://json-schema.org/draft/2020-12/schema");

    /// <summary>JSON Schema Draft-07, for SchemaStore, VS Code and AsyncAPI.</summary>
    public static JsonSchemaProfile Draft07 { get; } = new("draft-07", "http://json-schema.org/draft-07/schema#");

    /// <summary>The profile's name.</summary>
    public string Name { get; }

    /// <summary>The value of the <c>$schema</c> keyword.</summary>
    public string Dialect { get; }
}
