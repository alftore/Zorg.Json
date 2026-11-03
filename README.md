# Zorg.Json

A fluent, Zod-inspired JSON schema library for .NET. Define strict schemas, validate JSON, and get precise errors.

Status: early development. Only string schemas exist so far, and the API may change.

```csharp
StringSchema name = Z.String().Trim().MinLength(1).MaxLength(100);

ParseResult<string> result = name.SafeParse("\"  Ada  \"");   // Value: "Ada"
JsonObject schema = name.ToJsonSchema();                       // JSON Schema 2020-12
```

## Scratch space

Local scratch and temporary files go in `.tmp/`. That folder is gitignored.

## License

MIT. See [LICENSE](LICENSE).
