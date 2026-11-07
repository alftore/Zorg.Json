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

Draft 2020-12, Draft 2019-09, Draft-07, Draft-06, Draft-04 and OpenAPI 3.0. The first five are detected from `$schema`. With no `$schema`, 2020-12 is assumed, and `JsonSchemaImportOptions.DefaultDialect` can override that. An OpenAPI 3.0 schema has no `$schema`, so it can only be chosen that way: see [OpenAPI 3.0](#openapi-30).

All core, applicator and validation keywords are honoured, including `not`, `if`/`then`/`else`, `dependent*`, `unevaluated*`, `$anchor` and `$ref` with cycles. Content keywords are annotations.

`format` follows the specification. In Draft 2019-09 and 2020-12 it is an annotation unless you set `Formats = FormatHandling.AssertKnown`, because the specification says checking "MUST be disabled by default". Draft-04, draft-06, draft-07 and OpenAPI 3.0 allow checking, so Zorg checks there unless you set `FormatHandling.AnnotateOnly`. Fluent schemas always check. With checking on, all 2020-12 formats are enforced. A format that parsing doesn't check is written to draft-07 and OpenAPI 3.0 as `x-inert-format`, with a diagnostic, because validators of those dialects may check it. A format Zorg doesn't know is written as it is, because its meaning isn't known: a validator that checks it can reject what parsing accepts.

A `$ref` reaches `#`, a JSON Pointer, an `$anchor` and any `$id` in the same document, by a relative, absolute or URN reference. Nothing is ever fetched. To reach other documents, pass them by URI, and they're bundled into the schema as embedded resources under `$defs`, as JSON Schema describes for bundling:

```csharp
JsonValueSchema schema = Z.FromJsonSchema(text, new()
{
    Documents = new Dictionary<string, string> { ["https://example.com/address.json"] = addressText },
});
```

The emitted schema is one document that needs nothing else. A document has to use the dialect of the schema, and it answers to the URI it was given as well as its own `$id`.

`$dynamicRef` and `$dynamicAnchor` work, as do `$recursiveRef` and `$recursiveAnchor` in Draft 2019-09, which are written as `$dynamicRef` and `$dynamicAnchor` when emitted. Where they can't work, they're left out with a diagnostic and handled like any rule that can't be stated exactly: draft-07, OpenAPI 3.0, with `ReferencePrefix`, with rewriters, and inside a larger fluent schema, where the import shares a resource with what surrounds it. A subschema with `$dynamicRef` counts as inexact, because what it lands on depends on the dynamic scope, so `not` around it is left out when emitted. A `$dynamicRef` that doesn't land on a `$dynamicAnchor` of its name is a plain reference, and is written as `$ref`. `$dynamicRef` or `$recursiveRef` beside `$ref` in one schema object is `unsupported_keyword`: put one of them in an `allOf`. Unknown and `x-*` keywords are kept and emitted again.

**Nothing is dropped silently.** A schema that can't be honoured throws `JsonSchemaImportException`, with a stable `Code` and the schema location:

| Code | Meaning |
|---|---|
| `unsupported_keyword` | `$dynamicRef` or `$recursiveRef` beside `$ref` in one schema object |
| `unsupported_dialect` | Draft-03 or a custom meta-schema. Also a boolean `nullable` in a document with no `$schema`, while `DefaultDialect` is left alone, because that is OpenAPI 3.0 and `nullable` would be ignored |
| `unresolved_ref` | A `$ref` to something that isn't in the document, or that points at nothing. Nothing is ever fetched |
| `ref_cycle` | A reference cycle that never consumes input, such as `{ "$ref": "#" }` |
| `invalid_pattern` | A pattern that isn't valid ECMA-262 |
| `invalid_schema` | A keyword with an invalid value, such as an empty array-form `items`. Also text that isn't valid Unicode where Zorg reads it: an unpaired surrogate such as `\ud800` in a keyword, a property name, `$ref`, `$schema` or `required`. A `const`, `enum` or unknown keyword that holds one is only kept. Emitting the schema again leaves such a `const` or `enum` out with a diagnostic (and the schema is inexact under `not`, `if` and `oneOf`), because it can't be written as JSON from .NET |
| `limit_exceeded` | The document is too deep or too large for the options, or its patterns add up to more than `MaxTotalPatternLength` |

`unevaluated*` looks at what the subschemas beside it evaluated, as the specification says. It only runs on a value that passed everything else, and it runs the branches of `anyOf`, `oneOf` and `if` a second time to see which passed, so a schema with these keywords costs more and a refinement in such a branch runs twice. Emitted to a dialect without them, they're left out. They're also left out when an `if` beside them can't be written, with a diagnostic, because what that `if` evaluates would be lost and the written schema would reject what parsing accepts.

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

The compliance tests run the official JSON-Schema-Test-Suite for all five drafts, with `optional/format`, `bignum` and `float-overflow`, and `zeroTerminatedFloats` for Draft-04. Every case either passes, or its schema is refused with the exact code listed in `tests/Zorg.Json.ComplianceTests/suite-manifest.json`. No case gets a wrong verdict.

Every imported schema is also emitted, imported again and rechecked, so emitting doesn't change what a document means. The one exception is the Draft-04 integer, below.

## Patterns

Patterns are ECMA-262 in Unicode mode, as JSON Schema says, for both import and `.Pattern(...)`. They're translated to .NET, using the linear-time engine where possible and a 100 ms timeout otherwise.

Translating costs milliseconds for patterns with `\p{L}` and the like, so a compiled pattern is shared by its text between import, validation and every schema that uses it. The cache is bounded, and a pattern that fails to compile isn't kept. Import also counts each distinct pattern text towards `MaxTotalPatternLength`, 65,536 UTF-16 units by default, on top of the limit for each pattern. Raise it for a document you trust.

Unicode mode is strict: `\-` outside a character class, or a bare `{`, is an error. Some syntax isn't available: `\p{Script=...}`, and binary properties other than `Any`, `ASCII` and `Assigned`. .NET-only syntax such as `(?i)` is rejected.

One known difference: ECMA clears a group's capture on each loop iteration, and .NET doesn't. This only matters for a backreference to a group inside a quantifier, such as `(?:(a)|b)*\1`.

## Emitting an imported schema

Imported schemas emit as Draft 2020-12 unless you pass a `Target`. A Draft-07 or Draft-04 document that had keywords beside `$ref`, or keywords the draft ignores, keeps them under an `x-inert-` prefix, so emitting can't change what the schema accepts.

A `$ref` can point at text the emitted dialect doesn't read as a schema: under `$defs` in Draft-07, under an unknown keyword, or under a sibling of `$ref` that is ignored. Such a target is read with the rules of its draft like any other, and written again under `$defs`, named after its entry in `$defs`, `definitions` or `components/schemas` where it has one, and `target-1` and so on otherwise. The reference is changed to match. The original text stays where it was, and where 2020-12 would read it as a schema it is under the `x-inert-` prefix, so `$defs` becomes `x-inert-$defs`. A legacy `$id` with a fragment, such as `http://x/y#frag`, is written as `$id` and `$anchor`. A fragment that isn't a plain name is `invalid_schema`.

An imported schema can be a part of a fluent one, as in `Z.Array(Z.FromJsonSchema(text))`. Its `$defs` move to the `$defs` of the whole document, under names that are free there (`a`, then `a2`). With `ReferencePrefix`, the definitions of an imported schema go to `EmitResult.Definitions`, and nothing is left under `$defs`. A schema with an `$id` of its own is a resource: its `$defs` stay where they are, and its references are read from it wherever it is written. Used twice, the second copy gets an `$id` with a suffix (`-2`), and repeats no `$anchor`.

Every reference is written last, from where its target ended up in the finished document, so a place that moved or was respelled needs nothing from you. A target that wasn't written, or that the reference can't reach, is written again under the definitions, with the name given above: draft-04 integers leave out `not`, OpenAPI 3.0 has no `if` or `patternProperties`, and `oneOf` can become `anyOf`. Inside a resource with an `$id`, it goes under the definitions of that resource. With `ReferencePrefix` the root goes wherever you put it, so nothing can point into it, and each target there goes to `EmitResult.Definitions`, the root itself included. Those definitions join the ones from other schemas, as in an OpenAPI document, so the name of an imported definition ends in a hash of its content and of everything it reaches (`a-3fa9c21e7d10b6e4`, or `target-3fa9c21e7d10b6e4` for a place with no name). It depends on nothing around the definition, so two different ones never clash, and the same one has the same name wherever it's used. The plain name can't be kept where nothing clashes: each schema is emitted alone, so that would depend on which endpoints exist and in what order. Names given with `.Named` stay as they are. A boolean definition is written as `{}` or `{ "not": {} }` there. OpenAPI 3.0 has no `$id` or `$anchor`, so an imported schema writes neither there, and references are pointers from the document.

Draft-04 can't be emitted, and no other dialect can tell `1.0` from `1`, so a Draft-04 integer gets looser when it's emitted: `{ "type": "integer" }` accepts `1.0` afterwards. The input schema must never reject what parsing accepts, so an integer inside `not` or `oneOf` costs more. `not` is left out and `oneOf` becomes `anyOf`, as for any subschema that can't be stated exactly, and `EmitResult.Diagnostics` lists them. Six cases of the official suite change this way, marked `LooserAfterEmit` in the manifest, and each one goes from rejected to accepted.
