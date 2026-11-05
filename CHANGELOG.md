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
- Packages now link to the repository.
- Exceeding an evaluation limit now fails the whole parse. Before, a branch under `if`, `oneOf` or `contains` that ran out of budget, or a regex that timed out under `not` or `if`, counted as "didn't match" and could turn into acceptance. There is now a budget over all steps as well as the speculative one, and the guide lists the limits.
- Added the issue code `too_many_issues`. A context keeps 100 issues, and `unrecognized_keys` lists 20 names.
- `format: regex` accepts at most 1,024 characters and is charged to the budget.
- `Pattern(Regex)` no longer takes `regex.ToString()` as the ECMA-262 `pattern`. Pass `ecmaPattern` to state one.
- Fixed `uuid`, `iri`, `uri-template`, `idn-hostname` and `relative-json-pointer` verdicts, translated regex group names that collided, and percent-encoded astral property names in `SchemaLocation`.

## 0.1.0-preview.1

First preview.
