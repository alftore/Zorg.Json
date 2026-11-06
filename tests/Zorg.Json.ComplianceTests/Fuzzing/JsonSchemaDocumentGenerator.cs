using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Zorg.Json.Import;

namespace Zorg.Json.ComplianceTests.Fuzzing;

/// <summary>Builds random JSON Schema documents for <c>Z.FromJsonSchema</c>, including ones no dialect allows.</summary>
internal sealed class JsonSchemaDocumentGenerator(Random random)
{
    private static readonly string[] TypeNames = ["string", "number", "integer", "boolean", "null", "array", "object", "any", "String", ""];

    private static readonly string[] Formats = ["email", "uuid", "uri", "date-time", "date", "time", "ipv4", "ipv6", "hostname", "regex", "json-pointer", "byte", "int32", "double", "nonsense"];

    private static readonly string[] Patterns = ["^a", "a$", "^[a-z]+$", "(a+)+$", "\\d+", "^\\p{L}+$", "(?=a)", "[", "\\", "(a)\\1", "^x-", ""];

    private static readonly string[] Names = ["a", "b", "name", "k", "x-a", "", "~", "a/b", "kind"];

    private static readonly string[] Dialects =
    [
        "https://json-schema.org/draft/2020-12/schema", "http://json-schema.org/draft-07/schema#", "http://json-schema.org/draft-06/schema#",
        "http://json-schema.org/draft-04/schema#", "https://json-schema.org/draft/2019-09/schema", "http://example.com/unknown", "",
    ];

    private readonly Random _random = random;
    private readonly List<string> _refs = ["#", "#/$defs/d0", "#/definitions/d0", "#/properties/a", "#/$defs/missing", "#/items", "#/$defs/d0/properties/a", "other.json", "#/$defs/d1", "#foo", "#/$defs/a~1b"];

    public JsonSchemaImportDialect Dialect { get; private set; }

    public JsonObject Root()
    {
        Dialect = _random.Next(5) is var pick && pick == 4 ? JsonSchemaImportDialect.Draft201909 : (JsonSchemaImportDialect)pick;
        JsonObject root = Schema(0);

        if (_random.Next(3) == 0)
        {
            root["$schema"] = Dialects[_random.Next(Dialects.Length)];
        }

        JsonObject defs = new();

        for (int index = 0; index < _random.Next(3); index++)
        {
            defs["d" + index] = Schema(1);
        }

        root[_random.Next(2) == 0 ? "$defs" : "definitions"] = defs;
        return root;
    }

    private JsonNode Any(int depth)
    {
        return _random.Next(12) == 0 ? JsonValue.Create(_random.Next(2) == 0) : Schema(depth);
    }

    private JsonObject Schema(int depth)
    {
        JsonObject schema = new();
        int count = depth > 4 ? _random.Next(0, 3) : _random.Next(0, 6);

        for (int index = 0; index < count; index++)
        {
            Keyword(schema, depth);
        }

        return schema;
    }

    private JsonNode Value()
    {
        return _random.Next(12) switch
        {
            0 => null!,
            1 => true,
            2 => false,
            3 => 0,
            4 => 1,
            5 => 1.5,
            6 => "a",
            7 => "",
            8 => new JsonArray(1, "a", null),
            9 => new JsonObject { ["a"] = 1 },
            10 => JsonNode.Parse("1e999")!,
            _ => -1,
        };
    }

    private void Keyword(JsonObject schema, int depth)
    {
        int next = depth + 1;

        switch (_random.Next(48))
        {
            case 0:
                schema["type"] = TypeNames[_random.Next(TypeNames.Length)];
                break;
            case 1:
                schema["type"] = new JsonArray(TypeNames[_random.Next(TypeNames.Length)], TypeNames[_random.Next(TypeNames.Length)]);
                break;
            case 2:
                schema["properties"] = Map(next);
                break;
            case 3:
                schema["required"] = new JsonArray(Names[_random.Next(Names.Length)], Names[_random.Next(Names.Length)]);
                break;
            case 4:
                schema["additionalProperties"] = Any(next);
                break;
            case 5:
                schema["patternProperties"] = new JsonObject { [Patterns[_random.Next(Patterns.Length)]] = Any(next) };
                break;
            case 6:
                schema["items"] = _random.Next(3) == 0 ? new JsonArray(Any(next), Any(next)) : Any(next);
                break;
            case 7:
                schema["prefixItems"] = new JsonArray(Any(next), Any(next));
                break;
            case 8:
                schema["additionalItems"] = Any(next);
                break;
            case 9:
                schema["minItems"] = _random.Next(-1, 4);
                break;
            case 10:
                schema["maxItems"] = _random.Next(-1, 4);
                break;
            case 11:
                schema["contains"] = Any(next);
                break;
            case 12:
                schema["minContains"] = _random.Next(-1, 3);
                break;
            case 13:
                schema["maxContains"] = _random.Next(-1, 3);
                break;
            case 14:
                schema["uniqueItems"] = _random.Next(2) == 0;
                break;
            case 15:
                schema["enum"] = new JsonArray(Value(), Value());
                break;
            case 16:
                schema["const"] = Value();
                break;
            case 17:
                schema[_random.Next(2) == 0 ? "minimum" : "maximum"] = _random.Next(-2, 100);
                break;
            case 18:
                schema["exclusiveMinimum"] = _random.Next(2) == 0 ? JsonValue.Create(true) : JsonValue.Create(_random.Next(-2, 10));
                break;
            case 19:
                schema["exclusiveMaximum"] = _random.Next(2) == 0 ? JsonValue.Create(false) : JsonValue.Create(_random.Next(-2, 10));
                break;
            case 20:
                schema["multipleOf"] = _random.Next(-1, 4) * 0.5;
                break;
            case 21:
                schema["minLength"] = _random.Next(-1, 5);
                break;
            case 22:
                schema["maxLength"] = _random.Next(-1, 5);
                break;
            case 23:
                schema["pattern"] = Patterns[_random.Next(Patterns.Length)];
                break;
            case 24:
                schema["format"] = Formats[_random.Next(Formats.Length)];
                break;
            case 25:
                schema["allOf"] = List(next);
                break;
            case 26:
                schema["anyOf"] = List(next);
                break;
            case 27:
                schema["oneOf"] = List(next);
                break;
            case 28:
                schema["not"] = Any(next);
                break;
            case 29:
                schema["if"] = Any(next);
                break;
            case 30:
                schema["then"] = Any(next);
                break;
            case 31:
                schema["else"] = Any(next);
                break;
            case 32:
                schema[_random.Next(4) == 0 ? "$recursiveRef" : _random.Next(3) == 0 ? "$dynamicRef" : "$ref"] = _refs[_random.Next(_refs.Count)];
                break;
            case 33:
                schema["$id"] = _random.Next(3) == 0 ? "http://example.com/root" : "sub" + _random.Next(3) + ".json";
                break;
            case 34:
                schema["$anchor"] = "foo";
                break;
            case 35:
                schema["dependentRequired"] = new JsonObject { [Names[_random.Next(Names.Length)]] = new JsonArray(Names[_random.Next(Names.Length)]) };
                break;
            case 36:
                schema["dependentSchemas"] = new JsonObject { [Names[_random.Next(Names.Length)]] = Any(next) };
                break;
            case 37:
                schema["dependencies"] = new JsonObject { [Names[_random.Next(Names.Length)]] = _random.Next(2) == 0 ? new JsonArray("a") : Any(next) };
                break;
            case 38:
                schema["propertyNames"] = Any(next);
                break;
            case 39:
                schema["unevaluatedProperties"] = Any(next);
                break;
            case 40:
                schema["unevaluatedItems"] = Any(next);
                break;
            case 41:
                schema["nullable"] = _random.Next(2) == 0;
                break;
            case 42:
                schema["minProperties"] = _random.Next(-1, 3);
                break;
            case 43:
                schema["maxProperties"] = _random.Next(-1, 3);
                break;
            case 44:
                schema["default"] = Value();
                break;
            case 45:
                schema["x-unknown"] = Value();
                break;
            case 46:
                schema["$defs"] = new JsonObject { ["d0"] = Any(next) };
                break;
            default:
                schema[new[] { "title", "description", "examples", "deprecated", "readOnly", "$comment", "contentEncoding", "contentMediaType" }[_random.Next(8)]] = Value();
                break;
        }
    }

    private JsonObject Map(int depth)
    {
        JsonObject map = new();

        for (int index = 0; index < _random.Next(1, 4); index++)
        {
            map[Names[_random.Next(Names.Length)]] = Any(depth);
        }

        return map;
    }

    private JsonArray List(int depth) => new([.. Enumerable.Range(0, _random.Next(0, 4)).Select(_ => (JsonNode?)Any(depth))]);
}
