# Using and working on Zorg.Json

## Writing code that uses Zorg.Json

- A .NET type exists: `Schema.Object(AppJson.Default.Person)`, then `.Property(p => p.X, s => ...)`. Every property is already included, so only add rules.
- No .NET type: `Z.Object().Property("x", Z.String())`.
- Method names are JSON Schema keywords first: `MinLength`, `Maximum`, `MinItems`, `Pattern`. See `docs/reference.md` for the Zod equivalents.
- Schemas are immutable. Always use the return value, and store schemas in `static readonly` fields.
- Prefer `SafeParse`, which returns `ParseResult<T>`. Read `.Value` only after checking `.IsSuccess`.
- Unknown properties are rejected by default. Use `.Strip()` or `.Passthrough()` to change that.
- `ToJsonSchema()` describes the input. Pass `Direction = SchemaDirection.Output` for responses.
- There's no `Transform`. Use `ParseResult<T>.Map`.
- A self-reference inside a field initializer needs `!`: `Z.Lazy("tree", () => Tree!)`.
- Don't combine closed objects with `Z.AllOf`. Use `.Extend(other)`.

## Working on the repo

```bash
dotnet build Zorg.Json.slnx -nologo -v q
```

```bash
dotnet test --solution Zorg.Json.slnx --no-build
```

- `CONTRIBUTING.md` has the conventions. The build fails on any warning.
- The compliance tests need `git submodule update --init --depth 1`.
- Accept a snapshot by reading the `.received.` file, then renaming it to `.verified.`. Never bulk-accept without reading.
- `.tmp/design/01-core-model.md` holds the agreed design, where it exists on your machine.
- Don't add the Verify package: its build requires a licence declaration. Only use JsonSchema.Net in tests, and never Newtonsoft.Json.Schema.
- The input guarantee is the rule that matters most: the input schema must never reject what `SafeParse` accepts. `StructuredGuaranteeTests` checks it against an independent validator, so add new features there. Leaving a rule out is only safe in positive positions: see `SchemaExactness`.
- `SafeParse` must not throw for any input. `ReviewRegressionTests` holds the cases that once did. Catch around the parse or materialize call only, so that exceptions from a caller's refinement pass through.
