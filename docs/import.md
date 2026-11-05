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

Draft 2020-12, Draft-07, Draft-06 and Draft-04, detected from `$schema`. With no `$schema`, 2020-12 is assumed, and `JsonSchemaImportOptions` can override that.

All core, applicator and validation keywords are honoured, including `not`, `if`/`then`/`else`, `dependent*`, `$anchor` and local `$ref` with cycles. All 2020-12 formats are enforced, which JSON Schema leaves optional. Content keywords are annotations. Unknown and `x-*` keywords are kept and emitted again.

**Nothing is dropped silently.** A schema that can't be honoured throws `JsonSchemaImportException`, with a stable `Code` and the schema location:

| Code | Meaning |
|---|---|
| `unsupported_keyword` | `$dynamicRef`, `$dynamicAnchor`, or `unevaluated*` beside an in-place applicator such as `allOf` |
| `unsupported_dialect` | Draft-03, 2019-09, OpenAPI 3.0 or a custom meta-schema |
| `unresolved_ref` | A `$ref` that isn't local, or points at nothing. Nothing is ever fetched |
| `ref_cycle` | A reference cycle that never consumes input, such as `{ "$ref": "#" }` |
| `invalid_pattern` | A pattern that isn't valid ECMA-262 |
| `invalid_schema` | A keyword with an invalid value |
| `limit_exceeded` | The document is too deep or too large for the options |

`unevaluated*` works when no in-place applicator sits beside it, where it's equivalent to `additionalProperties` or `items`.

The defaults for depth, node count and pattern length are in `JsonSchemaImportOptions`. Lower them for documents you don't trust.

## Draft-04

Draft-04 documents, such as those Zod writes for `target: "draft-04"`, are read with Draft-04's own meaning:

- `id` is the identifier, and `$id` is an unknown keyword.
- `exclusiveMinimum` and `exclusiveMaximum` are booleans that change `minimum` and `maximum`. A number, or a boolean with no `minimum` or `maximum` beside it, is `invalid_schema`.
- `definitions`, `dependencies`, array-form `items` with `additionalItems`, and ignored `$ref` siblings work as in Draft-07.
- `const`, `contains`, `propertyNames`, `examples`, `if`/`then`/`else`, `$comment`, `readOnly` and the other later keywords do nothing.
- A boolean schema is `invalid_schema`, except as `additionalProperties` or `additionalItems`.
- An integer is a number without a fraction or exponent, so `{ "type": "integer" }` rejects `1.0` and `1e2`.
- `format` works as in every other dialect, so formats Draft-04 doesn't define, such as `uuid`, are enforced too.

## Conformance

The compliance tests run the official JSON-Schema-Test-Suite for all four drafts, with `optional/format`, `bignum` and `float-overflow`, and `zeroTerminatedFloats` for Draft-04. Every case either passes, or its schema is refused with the exact code listed in `tests/Zorg.Json.ComplianceTests/suite-manifest.json`. No case gets a wrong verdict.

Every imported schema is also emitted, imported again and rechecked, so emitting doesn't change what a document means. The one exception is the Draft-04 integer, below.

## Patterns

Patterns are ECMA-262 in Unicode mode, as JSON Schema says, for both import and `.Pattern(...)`. They're translated to .NET, using the linear-time engine where possible and a 100 ms timeout otherwise.

Unicode mode is strict: `\-` outside a character class, or a bare `{`, is an error. Some syntax isn't available: `\p{Script=...}`, and binary properties other than `Any`, `ASCII` and `Assigned`. .NET-only syntax such as `(?i)` is rejected.

One known difference: ECMA clears a group's capture on each loop iteration, and .NET doesn't. This only matters for a backreference to a group inside a quantifier, such as `(?:(a)|b)*\1`.

## Emitting an imported schema

Imported schemas emit as Draft 2020-12 unless you pass a `Target`. A Draft-07 or Draft-04 document that had keywords beside `$ref`, or keywords the draft ignores, keeps them under an `x-inert-` prefix, so emitting can't change what the schema accepts.

Draft-04 can't be emitted, and no other dialect can tell `1.0` from `1`, so a Draft-04 integer gets looser when it's emitted: `{ "type": "integer" }` accepts `1.0` afterwards. The input schema must never reject what parsing accepts, so an integer inside `not` or `oneOf` costs more. `not` is left out and `oneOf` becomes `anyOf`, as for any subschema that can't be stated exactly, and `EmitResult.Diagnostics` lists them. Six cases of the official suite change this way, marked `LooserAfterEmit` in the manifest, and each one goes from rejected to accepted.
