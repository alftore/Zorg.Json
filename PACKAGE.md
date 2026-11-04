# Zorg.Json

One fluent C# definition that validates raw JSON with precise error paths, gives you a typed result, and emits JSON Schema you can serve from an API. Inspired by Zod, idiomatic in .NET, depends only on System.Text.Json, and works with Native AOT.

This is a preview, and the API may change.

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

## Packages

| Package | Contents |
|---|---|
| `Zorg.Json` | Schemas, validation, JSON Schema emission and import |
| `Zorg.Json.AspNetCore` | Request validation with problem details, serving schemas, options validation |
| `Zorg.Json.AspNetCore.OpenApi` | Schemas in the OpenAPI document |
| `Zorg.Json.Llm` | OpenAI, Anthropic and Gemini structured output profiles |

JSON Schema Draft 2020-12 is the default. Draft-07, OpenAPI 3.0 and OpenAPI 3.1 are also available. Every public member has XML documentation naming the JSON Schema keyword and the Zod equivalent.
