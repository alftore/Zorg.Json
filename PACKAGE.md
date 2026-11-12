# Zorg.Json

One fluent C# definition that validates raw JSON with precise error paths, gives you a typed result, and emits JSON Schema you can serve from an API. Inspired by Zod, idiomatic in .NET, depends only on System.Text.Json, and works with Native AOT.

This is a 0.x release, so the API can still change before 1.0. Breaking changes are listed in the [changelog](https://github.com/alftore/Zorg.Json/blob/main/CHANGELOG.md).

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
    Console.WriteLine(result.Issues.ToPrettyString());
}

JsonObject schema = PersonSchema.ToJsonSchema();   // JSON Schema 2020-12
```

Without a .NET type, use the Zod-style builder:

```csharp
ObjectSchema search = Z.Object()
    .Property("query", Z.String().Trim().MinLength(1))
    .Property("limit", Z.Integer().Minimum(1).Maximum(50).Default(10));
```

Objects remove unknown properties by default, and the schema leaves `additionalProperties` out. Call `.Strict()` to reject them.

A source generator in this package writes the schema from a type's validation attributes:

```csharp
[GenerateSchema(typeof(AppJson))]
public sealed partial record Signup([property: MinLength(3)] string UserName, [property: Range(13, 120)] int Age);

// AppJson also needs [JsonSerializable(typeof(Signup))].
ParseResult<Signup> result = Signup.Schema.SafeParse(json);
```

## How this is built

Zorg.Json is written with the help of AI agents, mainly Claude Code from Anthropic. The maintainer sets the direction and makes the design decisions, and agents do most of the implementation. This note covers the whole project, so individual commits don't carry a co-author line.

## Packages

| Package | Contents |
|---|---|
| `Zorg.Json` | Schemas, validation, JSON Schema emission and import, sample data |
| `Zorg.Json.AspNetCore` | Request validation with problem details, serving schemas, options validation |
| `Zorg.Json.AspNetCore.OpenApi` | Schemas in the OpenAPI document |
| `Zorg.Json.Llm` | OpenAI, Anthropic and Gemini structured output profiles |
| `Zorg.Json.Extensions.AI` | Microsoft.Extensions.AI tools that check their arguments, and typed structured responses |

The packages run on .NET 10 and .NET 11. On .NET 11, typed schemas also read C# 15 unions and the new number types, such as `Decimal128`.

JSON Schema Draft 2020-12 is the default output. Draft-07, OpenAPI 3.0 and OpenAPI 3.1 are also available. Import reads 2020-12, 2019-09, Draft-07, Draft-06 and Draft-04, and passes the official JSON-Schema-Test-Suite: every case passes, or its schema is refused at import with a stable error code. Every public member has XML documentation naming the JSON Schema keyword and the Zod equivalent.
