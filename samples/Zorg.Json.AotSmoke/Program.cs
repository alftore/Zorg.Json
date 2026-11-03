using System;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Zorg.Json;
using Zorg.Json.Import;

// Runs the public API in a Native AOT binary. Any wrong result makes the exit code non-zero.
int failures = 0;

StringSchema schema = Z.String().MinLength(2).MaxLength(10).Pattern("^[a-z]+$");

ParseResult<string> valid = schema.SafeParse("\"ada\"");
Check("valid input", valid.IsSuccess && valid.Value == "ada");

ParseResult<string> trimmed = Z.String().Trim().MinLength(1).SafeParse("\"  ada  \"");
Check("trimmed input", trimmed.IsSuccess && trimmed.Value == "ada");

CheckIssue("too short", schema.SafeParse("\"a\""), IssueCodes.TooSmall);
CheckIssue("pattern mismatch", schema.SafeParse("\"ADA\""), IssueCodes.InvalidFormat);
CheckIssue("wrong type", schema.SafeParse("42"), IssueCodes.InvalidType);
CheckIssue("malformed JSON", schema.SafeParse("{"), IssueCodes.InvalidJson);

JsonObject emitted = schema.ToJsonSchema();
Check("$schema", Text(emitted, "$schema") == "https://json-schema.org/draft/2020-12/schema");
Check("type", Text(emitted, "type") == "string");
Check("pattern", Text(emitted, "pattern") == "^[a-z]+$");
Check("minLength", emitted["minLength"]?.GetValue<int>() == 2);
Check("maxLength", emitted["maxLength"]?.GetValue<int>() == 10);
Check("serialized schema", JsonNode.Parse(emitted.ToJsonString()) is JsonObject);

JsonValueSchema imported = Z.FromJsonSchema("""
    {
      "$defs": { "node": { "type": "object", "properties": { "next": { "$ref": "#/$defs/node" }, "at": { "format": "date-time" } } } },
      "$ref": "#/$defs/node"
    }
    """);
Check("imported valid", imported.SafeParse("""{ "next": { "at": "2026-11-03T12:00:00Z" } }""").IsSuccess);
Check("imported invalid", !imported.SafeParse("""{ "next": { "next": 1 } }""").IsSuccess);
Check("imported emission", imported.ToJsonSchema()["$ref"]?.GetValue<string>() == "#/$defs/node");

Schema<Point> bound = Z.FromJsonSchema("""{ "type": "object", "required": ["x"] }""").Bind(SmokeJson.Default.Point);
Check("bound value", bound.SafeParse("""{ "x": 3 }""").Value.X == 3);

try
{
    Z.FromJsonSchema("""{ "$ref": "#" }""");
    Check("ref cycle", false);
}
catch (JsonSchemaImportException exception)
{
    Check("ref cycle", exception.Code == JsonSchemaImportCodes.RefCycle);
}

Console.WriteLine(failures == 0 ? "AOT smoke test passed" : $"AOT smoke test failed: {failures} check(s)");
return failures == 0 ? 0 : 1;

void Check(string name, bool passed)
{
    if (!passed)
    {
        failures++;
        Console.Error.WriteLine($"FAILED: {name}");
    }
}

void CheckIssue(string name, ParseResult<string> result, string expectedCode) =>
    Check(name, !result.IsSuccess && result.Issues[0].Code == expectedCode && !string.IsNullOrEmpty(result.Issues[0].Message));

static string? Text(JsonObject schema, string keyword) => schema[keyword]?.GetValue<string>();

internal sealed record Point(int X);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Point))]
internal sealed partial class SmokeJson : JsonSerializerContext;
