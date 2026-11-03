# Zorg.Json

A fluent, Zod-inspired JSON schema library for .NET. Define strict schemas, validate JSON, and get precise errors.

Status: research phase. No public API yet.

```csharp
// Planned shape (not implemented yet)
var user = Zorg.Object()
    .Prop("name", Zorg.String().Min(3))
    .Prop("age", Zorg.Int().Range(0, 150));

var result = user.Parse(json);
```

## Scratch space

Local scratch and temporary files go in `.tmp/`. That folder is gitignored.

## License

MIT. See [LICENSE](LICENSE).
