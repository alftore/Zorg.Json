# Zorg.Json

One fluent C# definition that validates raw JSON with precise error paths, gives you a typed result, and emits JSON Schema you can serve from an API. Inspired by Zod, idiomatic in .NET, depends only on System.Text.Json, and works with Native AOT.

Status: 0.x. The API can still change before 1.0. Breaking changes are listed in the [changelog](CHANGELOG.md).

```csharp
public sealed record Person(string Name, string? Nickname, int Age);

[JsonSerializable(typeof(Person))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
public sealed partial class AppJson : JsonSerializerContext;

static readonly ObjectSchema<Person> PersonSchema = Schema.Object(AppJson.Default.Person)
    .Property(p => p.Name, s => s.Trim().MinLength(1).MaxLength(100))
    .Property(p => p.Age, s => s.Minimum(0).Maximum(150));

ParseResult<Person> result = PersonSchema.SafeParse(json);

if (result.IsSuccess)
{
    Console.WriteLine(result.Value.Name);
}
else
{
    Console.WriteLine(result.Issues.ToPrettyString());   // ✖ Must be at least 0.
                                                         //   → at /age
}

JsonObject schema = PersonSchema.ToJsonSchema();         // JSON Schema 2020-12
```

Without a .NET type, use the Zod-style builder:

```csharp
StringSchema name = Z.String().Trim().MinLength(1).MaxLength(100);

ObjectSchema search = Z.Object()
    .Property("query", name)
    .Property("limit", Z.Integer().Minimum(1).Maximum(50).Default(10));
```

## Why

- **One definition.** The rules, the typed result and the published schema can't drift apart.
- **Tolerant by default, strict when you ask.** Unknown properties are removed, as in Zod and System.Text.Json, so clients can add fields without breaking. `.Strict()` rejects them. A typed schema covers every property of the type.
- **Errors you can act on.** Stable codes and JSON Pointer paths, on raw JSON before deserialization.
- **Honest schemas.** The emitted schema accepts everything parsing accepts, checked against an independent validator. What it can't express is reported, not dropped.

## Checking `format`

In JSON Schema, `format` is an annotation by default. Draft 2019-09 and later say validators must not check it unless asked. So an imported 2020-12 schema accepts `"nope"` for `"format": "email"`, as most JSON Schema validators do. To check formats, ask for it:

```csharp
JsonValueSchema schema = Z.FromJsonSchema(text, new() { Formats = FormatHandling.AssertKnown });
```

Fluent schemas such as `Z.String().Email()` always check. Imported draft-04, draft-06 and draft-07 schemas check too, which those drafts allow.

## Packages

| Package | Contents |
|---|---|
| `Zorg.Json` | Schemas, validation, JSON Schema emission and import, sample data |
| `Zorg.Json.AspNetCore` | Request validation with problem details, serving schemas, options validation |
| `Zorg.Json.AspNetCore.OpenApi` | Schemas in the OpenAPI document |
| `Zorg.Json.Llm` | OpenAI, Anthropic and Gemini structured output profiles |
| `Zorg.Json.Extensions.AI` | Microsoft.Extensions.AI tools that check their arguments, and typed structured responses |

The packages run on .NET 10 and .NET 11. On .NET 11, typed schemas also read C# 15 unions and the new number types, such as `Decimal128`.

JSON Schema Draft 2020-12 is the default output. Draft-07, OpenAPI 3.0 and OpenAPI 3.1 are also available. Import reads 2020-12, 2019-09, Draft-07, Draft-06 and Draft-04.

## Documentation

- [Guide](docs/guide.md)
- [Keyword and Zod reference](docs/reference.md)
- [ASP.NET Core](docs/aspnetcore.md)
- [LLM structured output](docs/llm.md)
- [Importing JSON Schema](docs/import.md)
- [Known limits](docs/limits.md)

## Conformance

Imported schemas run against the official [JSON-Schema-Test-Suite](https://github.com/json-schema-org/JSON-Schema-Test-Suite) in CI. Every case either passes, or its schema is refused at import with a stable error code, so no case gets a wrong verdict.

| Draft | Passed | Refused at import | Failed |
|---|---|---|---|
| 2020-12 | 2,236 | 9 | 0 |
| 2019-09 | 2,195 | 9 | 0 |
| Draft-07 | 1,775 | 4 | 0 |
| Draft-06 | 1,274 | 4 | 0 |
| Draft-04 | 922 | 4 | 0 |

The refused cases are a `$ref` to an official meta-schema and custom meta-schemas. Passed counts include the optional format, big number and float overflow tests, which run with `format` checking turned on: 2020-12 leaves it off by default, as the specification requires. CI also compares Zorg with a second independent validator on generated documents.

## How this is built

Zorg.Json is written with the help of AI agents, mainly Claude Code from Anthropic. The maintainer sets the direction and makes the design decisions, and agents do most of the implementation. This note covers the whole project, so individual commits don't carry a co-author line.

## Contributing

Issues are welcome. Pull requests are open to the maintainer only. See [CONTRIBUTING.md](CONTRIBUTING.md) for why, and for how to build and test.

## License

MIT. See [LICENSE](LICENSE).
