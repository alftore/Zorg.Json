# Importing JSON Schema

`Z.FromJsonSchema` turns a JSON Schema document into a Zorg schema on the same model as the fluent API.

```csharp
JsonValueSchema schema = Z.FromJsonSchema(documentText);   // or a JsonElement

ParseResult<JsonElement> result = schema.SafeParse(json);

// Validate with the document, then deserialize.
Schema<WeatherArgs> arguments = schema.Bind(AppJson.Default.WeatherArgs);
```

Imported schemas follow the document, not Zorg's fluent defaults: an absent `additionalProperties` means open, and `default` doesn't fill in values, because in JSON Schema it's only an annotation.

## What's supported

Draft 2020-12, Draft-07 and Draft-06, detected from `$schema`. With no `$schema`, 2020-12 is assumed, and `JsonSchemaImportOptions` can override that.

All core, applicator and validation keywords are honoured, including `not`, `if`/`then`/`else`, `dependent*`, `$anchor` and local `$ref` with cycles. All 2020-12 formats are enforced, which JSON Schema leaves optional. Content keywords are annotations. Unknown and `x-*` keywords are kept and emitted again.

**Nothing is dropped silently.** A schema that can't be honoured throws `JsonSchemaImportException`, with a stable `Code` and the schema location:

| Code | Meaning |
|---|---|
| `unsupported_keyword` | `$dynamicRef`, `$dynamicAnchor`, or `unevaluated*` beside an in-place applicator such as `allOf` |
| `unsupported_dialect` | Draft-04, 2019-09, OpenAPI 3.0 or a custom meta-schema |
| `unresolved_ref` | A `$ref` that isn't local, or points at nothing. Nothing is ever fetched |
| `ref_cycle` | A reference cycle that never consumes input, such as `{ "$ref": "#" }` |
| `invalid_pattern` | A pattern that isn't valid ECMA-262 |
| `invalid_schema` | A keyword with an invalid value |
| `limit_exceeded` | The document is too deep or too large for the options |

`unevaluated*` works when no in-place applicator sits beside it, where it's equivalent to `additionalProperties` or `items`.

The defaults for depth, node count and pattern length are in `JsonSchemaImportOptions`. Lower them for documents you don't trust.

## Conformance

The compliance tests run the official JSON-Schema-Test-Suite for all three drafts, with `optional/format`, `bignum` and `float-overflow`. Every case either passes, or its schema is refused with the exact code listed in `tests/Zorg.Json.ComplianceTests/suite-manifest.json`. No case gets a wrong verdict.

Every imported schema is also emitted, imported again and rechecked, so emitting doesn't change what a document means.

## Patterns

Patterns are ECMA-262 in Unicode mode, as JSON Schema says, for both import and `.Pattern(...)`. They're translated to .NET, using the linear-time engine where possible and a 100 ms timeout otherwise.

Unicode mode is strict: `\-` outside a character class, or a bare `{`, is an error. Some syntax isn't available: `\p{Script=...}`, and binary properties other than `Any`, `ASCII` and `Assigned`. .NET-only syntax such as `(?i)` is rejected.

One known difference: ECMA clears a group's capture on each loop iteration, and .NET doesn't. This only matters for a backreference to a group inside a quantifier, such as `(?:(a)|b)*\1`.

## Emitting an imported schema

Imported schemas emit as Draft 2020-12 unless you pass a `Target`. A Draft-07 document that had keywords beside `$ref`, or keywords the draft ignores, keeps them under an `x-inert-` prefix, so emitting can't change what the schema accepts.
