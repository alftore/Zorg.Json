using Zorg.Json.Model;

namespace Zorg.Json;

/// <summary>
/// The untyped, Zod-style entry point. Add <c>using static Zorg.Json.Z;</c> to write <c>String()</c> instead of
/// <c>Z.String()</c>.
/// </summary>
public static class Z
{
    /// <summary>Creates a schema for JSON strings. JSON Schema: <c>type: string</c>. Zod equivalent: <c>z.string()</c>.</summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// StringSchema name = Z.String().MinLength(1).MaxLength(100);
    /// </code>
    /// </example>
    public static StringSchema String() => new(new SchemaNode { Types = JsonTypes.String });
}
