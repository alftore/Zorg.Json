# Changelog

## 0.1.0-preview.2

- Added `GenerateSample`, which produces a document that conforms to a schema.
- Added Draft-04 import.
- Added `JsonSchemaImportDialect.OpenApi30`, chosen through `DefaultDialect`. A boolean `nullable` in a document with no `$schema` is now `unsupported_dialect` rather than ignored.
- Added `MaxTotalPatternLength` to `JsonSchemaImportOptions`, and compiled patterns are now shared by text.
- Importing: text with an unpaired surrogate is `invalid_schema`, an empty array-form `items` is refused, and a `$ref` target the draft doesn't read as a schema is read with its draft's rules and emitted under `$defs`, so emitting keeps what the schema accepts.
- `[DefaultValue]` on a typed property now becomes the `default` keyword, and the property is optional unless it's `required`. It's an annotation, as the attribute is: parsing doesn't fill the value in.
- Imported schemas report `additionalProperties: false` as `unrecognized_keys`, with the keys listed, like fluent schemas.
- Fixed `SafeParse` throwing when a rewrite sat next to a string with a lone surrogate.
- `SafeParse` no longer throws on a lone surrogate in a property name (in `required`, `dependentRequired`, `dependentSchemas`, a discriminator or defaults), or on bytes that aren't UTF-8 against `const` or `enum`. The byte and stream overloads refuse input that isn't UTF-8 as `invalid_json`, and an unreadable string fails the parse like a limit, so `Z.Not(Z.String())` no longer accepts `"\ud800"`.
- Builders refuse text with an unpaired surrogate (names, patterns, `Const`, `Enum`, titles, `Meta`) with an `ArgumentException`, where the mistake is. `Contains` refuses a negative `minimum` or `maximum`.
- Equality and `uniqueItems` are structural and never throw or recurse, so a 50,000-deep item is safe. Items are hashed, independent of property order and number spelling, so `uniqueItems` over 50,000 objects takes milliseconds instead of minutes.
- Number schemas of a .NET type refuse a non-zero number that rounds to zero (`1e-400` for a `double`), as they refuse an overflow. Imported schemas stay exact.
- Emission: exactness depends on the target dialect and on `Strip` or `Default` with `MinProperties` and `MaxProperties`, so `not`, `if` and `oneOf` are no longer written around a subschema that the dialect or the input direction has to loosen (`Z.Not(Z.Object().Strip().MinProperties(1))` used to reject every object; draft-07 dropped `unevaluatedItems` in a `oneOf` branch). A `const` or `enum` with a lone surrogate in an imported schema is left out with a diagnostic.
- Emission stops with an `InvalidOperationException` after 100,000 subschemas, which names `Reused = Ref` and `.Named`. `GenerateSample` stops sooner on schemas that reject every candidate.
- Evaluation limits: `NormalizeUnicode` refuses a string with more than 64 combining marks in a row, a big `enum` is checked by hash, its issues share one list of values and its message lists 50, and `dependentRequired` and `required` look properties up in one index instead of once each.
- Importing a schema nested 100,000 levels deep is refused in milliseconds.
- Docs: "Differences from Zod" in the reference lists the differences that are on purpose, including the loss of data from `Strip` objects in `Z.AllOf`.
- Packages now link to the repository.
- Exceeding an evaluation limit now fails the whole parse. Before, a branch under `if`, `oneOf` or `contains` that ran out of budget, or a regex that timed out under `not` or `if`, counted as "didn't match" and could turn into acceptance. There is now a budget over all steps as well as the speculative one, and the guide lists the limits.
- Added the issue code `too_many_issues`. A context keeps 100 issues, and `unrecognized_keys` lists 20 names.
- `format: regex` accepts at most 1,024 characters and is charged to the budget.
- `Pattern(Regex)` no longer takes `regex.ToString()` as the ECMA-262 `pattern`. Pass `ecmaPattern` to state one.
- Fixed `uuid`, `iri`, `idn-hostname` and `relative-json-pointer` verdicts, translated regex group names that collided, and percent-encoded astral property names in `SchemaLocation`.
- Added `Validate(T)` and `ValidateAsync`, which check an object you already have.
- Added `Optional<T>` and `OptionalJsonConverter<T>`, which tell an absent property from `null`, for PATCH bodies.
- Added `Pattern(Regex, ecmaPattern)` for `[GeneratedRegex]`.
- Added `UnrepresentablePolicy.Callback` with `UnrepresentableHandler`.
- Added `EvaluationPath` and `SchemaLocation` to issues, and `ToTree()`.
- Past `maxItems` or `maxProperties`, only that issue is reported and the elements aren't validated.
- Union and conditional branches all see the same value. If several branches rewrite it, the last rewrite wins. `MinProperties`, `MaxProperties` and `UniqueItems` apply to the final value.
- `Zorg.Json.AspNetCore`: a `charset` other than UTF-8 gets a 415, handlers receive the validated JSON, so an absent `Optional<T>` stays absent, and problem details hold at most 100 issues.
- `Zorg.Json.Llm`: `ParseModelOutput` never throws on malformed output.

## 0.1.0-preview.1

First preview.
