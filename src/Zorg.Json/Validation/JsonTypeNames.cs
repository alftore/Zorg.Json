using Zorg.Json.Model;

namespace Zorg.Json.Validation;

internal static class JsonTypeNames
{
    public static string Name(JsonTypes type) => type switch
    {
        JsonTypes.Null => "null",
        JsonTypes.Boolean => "boolean",
        JsonTypes.Integer => "integer",
        JsonTypes.Number => "number",
        JsonTypes.String => "string",
        JsonTypes.Array => "array",
        _ => "object",
    };
}
