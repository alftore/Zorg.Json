using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Zorg.Json;
using Zorg.Json.Emission;
using Zorg.Json.Llm;
using Zorg.Json.Import;
using Zorg.Json.Sampling;

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

ObjectSchema<Person> person = Schema.Object(SmokeJson.Default.Person)
    .Property(p => p.Name, s => s.MinLength(1))
    .Property(p => p.Age, s => s.Minimum(0))
    .Property(p => p.Tags, s => s.MaxItems(2));

ParseResult<Person> typed = person.SafeParse("""{ "name": "Ada", "age": 36, "tags": ["a"], "kind": "Admin" }""");
Check("typed input", typed.IsSuccess && typed.Value.Name == "Ada" && typed.Value.Kind == Kind.Admin);

ParseResult<Person> invalid = person.SafeParse("""{ "name": "", "age": 36, "tags": [], "kind": "Nope", "extra": 1 }""");
Check("typed issues", invalid.Issues.Length == 3);
Check("typed pointer", invalid.Issues.Any(issue => issue.InstanceLocation == "/name"));
Check("typed schema", person.ToJsonSchema()["additionalProperties"]?.GetValue<bool>() == false);

Schema<Person> recursive = Schema.Lazy("person", () => (Schema<Person>)person);
Check("lazy", recursive.SafeParse("""{ "name": "Ada", "age": 36, "tags": [], "kind": "User" }""").IsSuccess);

ObjectSchema untyped = Z.Object()
    .Property("limit", Z.Integer().Coerce().Default(10))
    .Property("price", Z.Number<decimal>().MultipleOf(0.01m).Optional())
    .Property("tags", Z.Array(Z.String().Trim()).Optional());

Check("defaults", untyped.Parse("{}").GetProperty("limit").GetInt32() == 10);
Check("coercion", untyped.Parse("""{ "limit": "5" }""").GetProperty("limit").GetInt32() == 5);
Check("exact numbers", !untyped.SafeParse("""{ "price": 0.015 }""").IsSuccess);
Check("rewritten array", untyped.Parse("""{ "tags": [" a "] }""").GetProperty("tags")[0].GetString() == "a");
ObjectSchema answer = Z.Object()
    .Property("name", Z.String().MinLength(1))
    .Property("items", Z.Record(Z.Integer()).Optional())
    .Property("nick", Z.String().Optional());

EmitResult strict = answer.Emit(LlmProfiles.OpenAIStrict);
Check("relocated keyword", strict.Schema["properties"]?["name"]?["description"]?.GetValue<string>() == "Must also satisfy: minLength 1.");
Check("relocated schema", strict.Schema["properties"]?["items"]?.ToJsonString().Contains("additionalProperties", StringComparison.Ordinal) == true);
Check("linter", !answer.CheckCompatibility(LlmProfiles.OpenAIStrict).IsCompatible);
Check("model output", answer.ParseModelOutput(strict, """{ "name": "a", "items": null, "nick": null }""").IsSuccess);
Check("element dictionary", strict.Schema.ToJsonElementDictionary()["type"].ValueKind == JsonValueKind.String);
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

Check("sampled string", schema.GenerateSample().IsValid);
Check("sampled typed", person.SafeParse(person.GenerateSample().Value).IsSuccess);
Check("sampled import", imported.GenerateSample(new SampleOptions { MaxRecursionDepth = 1 }).IsValid);
Check("sampled pattern", Z.String().Pattern("^[a-z]{3}-\\d+$").GenerateSample().Value.GetString() == "aaa-0");
Check("sampled number", Z.FromJsonSchema("""{ "minimum": 0.15, "multipleOf": 0.1 }""").GenerateSample().Value.GetRawText() == "0.2");
Check("sampled unsatisfiable", !Z.Never().GenerateSample().IsValid);

ObjectSchema<PersonPatch> patch = Schema.Object(SmokeJson.Default.PersonPatch)
    .Property(p => p.Name, s => s.MinLength(1))
    .Property(p => p.Nick, s => s.MaxLength(5))
    .Property(p => p.Age, s => s.Minimum(0));

PersonPatch cleared = patch.Parse("""{ "nick": null, "age": 3 }""");
Check("optional absent", !cleared.Name.IsSet);
Check("optional null", cleared.Nick.IsSet && cleared.Nick.Value is null);
Check("optional value", cleared.Age.IsSet && cleared.Age.Value == 3);
Check("optional rule", !patch.SafeParse("""{ "name": "" }""").IsSuccess);
Check("optional null rejected", !patch.SafeParse("""{ "name": null }""").IsSuccess);
Check("optional not required", !patch.ToJsonSchema().ContainsKey("required"));
Check("optional omitted", JsonSerializer.Serialize(cleared, SmokeJson.Default.PersonPatch) == """{"nick":null,"age":3}""");
Check("validate instance", patch.Validate(new PersonPatch { Name = "Ada" }).IsSuccess && !patch.Validate(new PersonPatch { Name = "" }).IsSuccess);
Check("validate trimmed", Z.String().Trim().Validate("  a  ").Value == "a");

StringSchema generated = Z.String().Pattern(Patterns.Lower(), "^[a-z]+$");
Check("regex valid", generated.SafeParse("\"abc\"").IsSuccess && !generated.SafeParse("\"ABC\"").IsSuccess);
Check("regex emitted", generated.ToJsonSchema()["pattern"]?.GetValue<string>() == "^[a-z]+$");
EmitResult callback = Z.String().Refine(_ => true, "x").Emit(new JsonSchemaOptions
{
    Unrepresentable = UnrepresentablePolicy.Callback,
    UnrepresentableHandler = context => new JsonObject { ["x-refined"] = context.Value?.DeepClone() },
});
Check("unrepresentable callback", callback.Schema["x-refined"]?.GetValue<string>() == "x" && callback.Diagnostics.Length == 1);

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

internal enum Kind
{
    User,
    Admin,
}

internal sealed record Person(string Name, int Age, IReadOnlyList<string> Tags, Kind Kind);

internal sealed record Point(int X);

internal sealed class PersonPatch
{
    [JsonConverter(typeof(OptionalJsonConverter<string>))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<string> Name { get; init; }

    [JsonConverter(typeof(OptionalJsonConverter<string?>))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<string?> Nick { get; init; }

    [JsonConverter(typeof(OptionalJsonConverter<int>))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<int> Age { get; init; }
}

internal static partial class Patterns
{
    [GeneratedRegex("^[a-z]+$")]
    public static partial Regex Lower();
}

[JsonSerializable(typeof(Person))]
[JsonSerializable(typeof(Point))]
[JsonSerializable(typeof(PersonPatch))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, Converters = [typeof(JsonStringEnumConverter<Kind>)])]
internal sealed partial class SmokeJson : JsonSerializerContext;
