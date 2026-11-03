# Zorg.Json

One fluent C# definition that validates raw JSON with precise error paths, gives you a typed result, and emits JSON Schema you can serve from an API. Inspired by Zod, idiomatic in .NET, depends only on System.Text.Json, and works with Native AOT.

Status: pre-release. Nothing is on NuGet yet, and the API may change.

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
- **Strict by default.** Unknown properties are rejected, and a typed schema covers every property of the type.
- **Errors you can act on.** Stable codes and JSON Pointer paths, on raw JSON before deserialization.
- **Honest schemas.** The emitted schema accepts everything parsing accepts, checked against an independent validator. What it can't express is reported, not dropped.

## Packages

| Package | Contents |
|---|---|
| `Zorg.Json` | Schemas, validation, JSON Schema emission and import |
| `Zorg.Json.AspNetCore` | Request validation with problem details, serving schemas, options validation |
| `Zorg.Json.AspNetCore.OpenApi` | Schemas in the OpenAPI document |
| `Zorg.Json.Llm` | OpenAI, Anthropic and Gemini structured output profiles |

JSON Schema Draft 2020-12 is the default. Draft-07, OpenAPI 3.0 and OpenAPI 3.1 are also available.

## Documentation

- [Guide](docs/guide.md)
- [Keyword and Zod reference](docs/reference.md)
- [ASP.NET Core](docs/aspnetcore.md)
- [LLM structured output](docs/llm.md)
- [Importing JSON Schema](docs/import.md)

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Local scratch files go in `.tmp/`, which is gitignored.

## License

MIT. See [LICENSE](LICENSE).
