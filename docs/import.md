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

Draft 2020-12, Draft-07, Draft-06, Draft-04 and OpenAPI 3.0. The first four are detected from `$schema`. With no `$schema`, 2020-12 is assumed, and `JsonSchemaImportOptions.DefaultDialect` can override that. An OpenAPI 3.0 schema has no `$schema`, so it can only be chosen that way: see [OpenAPI 3.0](#openapi-30).

All core, applicator and validation keywords are honoured, including `not`, `if`/`then`/`else`, `dependent*`, `$anchor` and local `$ref` with cycles. All 2020-12 formats are enforced, which JSON Schema leaves optional. Content keywords are annotations. Unknown and `x-*` keywords are kept and emitted again.

**Nothing is dropped silently.** A schema that can't be honoured throws `JsonSchemaImportException`, with a stable `Code` and the schema location:

| Code | Meaning |
|---|---|
| `unsupported_keyword` | `$dynamicRef`, `$dynamicAnchor`, or `unevaluated*` beside an in-place applicator such as `allOf` |
| `unsupported_dialect` | Draft-03, 2019-09 or a custom meta-schema. Also a boolean `nullable` in a document with no `$schema`, while `DefaultDialect` is left alone, because that is OpenAPI 3.0 and `nullable` would be ignored |
| `unresolved_ref` | A `$ref` that isn't local, or points at nothing. Nothing is ever fetched |
| `ref_cycle` | A reference cycle that never consumes input, such as `{ "$ref": "#" }` |
| `invalid_pattern` | A pattern that isn't valid ECMA-262 |
| `invalid_schema` | A keyword with an invalid value, such as an empty array-form `items`. Also text that isn't valid Unicode where Zorg reads it: an unpaired surrogate such as `\ud800` in a keyword, a property name, `$ref`, `$schema` or `required`. A `const`, `enum` or unknown keyword that holds one is only kept. Emitting the schema again leaves such a `const` or `enum` out with a diagnostic (and the schema is inexact under `not`, `if` and `oneOf`), because it can't be written as JSON from .NET |
| `limit_exceeded` | The document is too deep or too large for the options, or its patterns add up to more than `MaxTotalPatternLength` |

`unevaluated*` works when no in-place applicator sits beside it, where it's equivalent to `additionalProperties` or `items`.

The defaults for depth, node count, pattern length and the total length of all patterns are in `JsonSchemaImportOptions`. Lower them for documents you don't trust.

## Draft-04

Draft-04 documents, such as those Zod writes for `target: "draft-04"`, are read with Draft-04's own meaning:

- `id` is the identifier, and `$id` is an unknown keyword.
- `exclusiveMinimum` and `exclusiveMaximum` are booleans that change `minimum` and `maximum`. A number, or a boolean with no `minimum` or `maximum` beside it, is `invalid_schema`.
- `definitions`, `dependencies`, array-form `items` with `additionalItems`, and ignored `$ref` siblings work as in Draft-07. An empty array-form `items` is `invalid_schema`, in Draft-07 and Draft-06 too, as `prefixItems` is in 2020-12: their meta-schemas want a non-empty array.
- `const`, `contains`, `propertyNames`, `examples`, `if`/`then`/`else`, `$comment`, `readOnly` and the other later keywords do nothing.
- A boolean schema is `invalid_schema`, except as `additionalProperties` or `additionalItems`.
- An integer is a number without a fraction or exponent, so `{ "type": "integer" }` rejects `1.0` and `1e2`.
- `format` works as in every other dialect, so formats Draft-04 doesn't define, such as `uuid`, are enforced too.

## OpenAPI 3.0

OpenAPI 3.0 documents, such as those Zod writes for `target: "openapi-3.0"`, are read with `DefaultDialect = JsonSchemaImportDialect.OpenApi30`:

```csharp
JsonValueSchema schema = Z.FromJsonSchema(text, new() { DefaultDialect = JsonSchemaImportDialect.OpenApi30 });
```

- `nullable: true` adds `null` to the `type` beside it, and does nothing without a `type`. It doesn't add `null` to an `enum`: as in OpenAPI 3.0.3, the `enum` has to list it. A `nullable` that isn't a boolean is `invalid_schema`.
- `type` is one name. An array, or the type `null`, is `invalid_schema`. `items` is one schema, never an array.
- `exclusiveMinimum` and `exclusiveMaximum` are booleans, and an integer has no fraction or exponent, as in Draft-04. Boolean schemas are `invalid_schema`, except as `additionalProperties`.
- `example` is read as the schema's one example. `readOnly`, `writeOnly`, `deprecated`, `discriminator`, `xml` and `externalDocs` are kept.
- `$ref` ignores its siblings. `definitions` is a container for references, which is where Zod puts them. A reference into `#/components/schemas/` works where the document holds `components`, next to the schema. Otherwise it is `unresolved_ref`, and the message says so.
- `const`, `contains`, `propertyNames`, `patternProperties`, `if`/`then`/`else`, `examples` and the other keywords OpenAPI 3.0 lacks do nothing. They are kept under the `x-inert-` prefix.
- A `$schema` in the document still wins, so a Draft-07 document stays Draft-07.

Nothing in a document says it is OpenAPI 3.0, apart from `nullable`. With no `$schema` and `DefaultDialect` left alone, a boolean `nullable` anywhere in the document fails with `unsupported_dialect`, and the message says to choose `OpenApi30`. Other OpenAPI 3.0 documents read as 2020-12 without a word, which gives the same answer except for integers with a fraction, such as `1.0`. A boolean `exclusiveMinimum` fails as `invalid_schema`.

## Conformance

The compliance tests run the official JSON-Schema-Test-Suite for all four drafts, with `optional/format`, `bignum` and `float-overflow`, and `zeroTerminatedFloats` for Draft-04. Every case either passes, or its schema is refused with the exact code listed in `tests/Zorg.Json.ComplianceTests/suite-manifest.json`. No case gets a wrong verdict.

Every imported schema is also emitted, imported again and rechecked, so emitting doesn't change what a document means. The one exception is the Draft-04 integer, below.

## Patterns

Patterns are ECMA-262 in Unicode mode, as JSON Schema says, for both import and `.Pattern(...)`. They're translated to .NET, using the linear-time engine where possible and a 100 ms timeout otherwise.

Translating costs milliseconds for patterns with `\p{L}` and the like, so a compiled pattern is shared by its text between import, validation and every schema that uses it. The cache is bounded, and a pattern that fails to compile isn't kept. Import also counts each distinct pattern text towards `MaxTotalPatternLength`, 65,536 UTF-16 units by default, on top of the limit for each pattern. Raise it for a document you trust.

Unicode mode is strict: `\-` outside a character class, or a bare `{`, is an error. Some syntax isn't available: `\p{Script=...}`, and binary properties other than `Any`, `ASCII` and `Assigned`. .NET-only syntax such as `(?i)` is rejected.

One known difference: ECMA clears a group's capture on each loop iteration, and .NET doesn't. This only matters for a backreference to a group inside a quantifier, such as `(?:(a)|b)*\1`.

## Emitting an imported schema

Imported schemas emit as Draft 2020-12 unless you pass a `Target`. A Draft-07 or Draft-04 document that had keywords beside `$ref`, or keywords the draft ignores, keeps them under an `x-inert-` prefix, so emitting can't change what the schema accepts.

A `$ref` can point at text the draft doesn't read as a schema: under `$defs` in Draft-07, under an unknown keyword, or under a sibling of `$ref` that is ignored. Such a target is read with the rules of its draft like any other, and written again under `$defs`, named after its entry in `$defs` or `components/schemas` where it has one, and `target-1` and so on otherwise. The reference is changed to match. The original text stays where it was, and where 2020-12 would read it as a schema it is under the `x-inert-` prefix, so `$defs` becomes `x-inert-$defs`. A legacy `$id` with a fragment, such as `http://x/y#frag`, is written as `$id` and `$anchor`. A fragment that isn't a plain name is `invalid_schema`.

Draft-04 can't be emitted, and no other dialect can tell `1.0` from `1`, so a Draft-04 integer gets looser when it's emitted: `{ "type": "integer" }` accepts `1.0` afterwards. The input schema must never reject what parsing accepts, so an integer inside `not` or `oneOf` costs more. `not` is left out and `oneOf` becomes `anyOf`, as for any subschema that can't be stated exactly, and `EmitResult.Diagnostics` lists them. Six cases of the official suite change this way, marked `LooserAfterEmit` in the manifest, and each one goes from rejected to accepted.
