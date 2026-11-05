# Zod comparison

Checks Zorg.Json against real Zod 4 to find bugs and undocumented differences. Zod is used only here. It is not a dependency of any package, and neither Node nor npm is needed to build or test the library.

The tool writes fixture files that are committed. `ZodComparisonTests` in `tests/Zorg.Json.ComplianceTests` reads them, so `dotnet test` runs the whole comparison without Node. The result is in [RESULTS.md](RESULTS.md).

## What is compared

- **Behaviour.** 100+ schema pairs (`catalogue.mjs` on the Zod side, `ZodCatalogue.cs` on the Zorg side, matched by name) run over 535 shared documents (`documents.mjs`). The documents are JSON text, so Zorg sees `1.0`, `1e2`, `-0`, big integers and duplicate keys exactly as written, while Zod sees `JSON.parse` of the same text. Random documents come from a fixed seed. For each pair and document the fixture records Zod's verdict, its output, and each issue as `code@/json/pointer`. Zod's path array is mapped to a JSON Pointer, and a `received undefined` `invalid_type` is recorded as `required`, which is Zorg's code for it.
- **Importing what Zod writes.** `z.toJSONSchema` for each schema, with targets `draft-2020-12`, `draft-07`, `draft-04` and `openapi-3.0`, `io` of `input` and `output`, and `reused` of `inline` and `ref`. `Z.FromJsonSchema` reads each one, and its verdicts are compared with Zod's. For `output`, Zod's own outputs must be accepted. `openapi-3.0` must be refused with `unsupported_dialect`, and its documents are also read to show what happens when they are not.
- **Zod reading what Zorg writes.** The test writes Zorg's JSON Schema (Draft 2020-12 and Draft-07, input direction) to `zorg-emitted.json`. `read-zorg.mjs` loads each with `z.fromJSONSchema` and writes Zod's verdicts to `zod-reads-zorg.json`. A refusal by Zod's reader is recorded as its own kind. There were none.

## Known differences

Every disagreement has to be matched by an entry in `known-differences.json`, or the test fails. So does an entry that matches nothing. An entry has:

| Field | Meaning |
|---|---|
| `id`, `category`, `reason`, `link` | What it is, and where it is documented. For an undocumented difference the link is the nearest doc. |
| `status` | `design` (documented), `undocumented-design` (intended, not in the docs), `zod-behaviour` (a Zod quirk), or `suspected_bug` (needs a `repro`) |
| `part` | `behaviour` (default), `interop-in`, `interop-out`, `openapi-refusal` or `openapi-misread`. Join several with `\|`. |
| `cases` | Case names, with `*` as a wildcard |
| `kinds` | `verdict`, `output`, `code`, `path`, `crash` or `refused` |
| `docTags`, `docs` | Match documents by tag (any of them) or by exact text. `negative-zero`, `integer-valued-float`, `bigint`, `overflow`, `underflow`, `lone-surrogate`, `astral`, `duplicate-key`, `proto-key` and the format names are derived from the text. |
| `zod`, `zorg` | `accept` or `reject`, for a verdict |
| `detail` | A regex over the issue lists, written `zod <code@path ...> / zorg <code@path ...>` |
| `targets`, `io` | For the interop parts |

The first entry that matches wins, so put a specific entry before a general one.

Two comparisons are explained without an entry. An interop disagreement is "same as native difference" when the imported or re-read schema gives the same verdict as the Zorg schema it came from, and that verdict already differs from Zod's in the behaviour part.

## Regenerating

You need Node 20 or later. From `tools/zod-compare`:

```bash
npm ci --ignore-scripts
node generate.mjs
```

This writes `tests/Zorg.Json.ComplianceTests/Fixtures/zod-comparison.json`. It is deterministic. Zod is pinned to exactly 4.6.7 in `package.json`, and `package-lock.json` holds the integrity hash. Scripts are off (`.npmrc`, and `--ignore-scripts`). To change the Zod version, edit `package.json`, run `npm install --ignore-scripts`, check the diff of the lock file, and update the version asserted in `ZodComparisonTests`.

Zorg's emitted schemas depend on `src/`, so after a change that affects emission, regenerate them from the repository root:

```bash
dotnet build tests/Zorg.Json.ComplianceTests -nologo -v q
ZORG_ZOD_WRITE=emitted dotnet test --project tests/Zorg.Json.ComplianceTests --no-build --filter-method "*ZodComparisonTests.WriteFixturesWhenAsked"
cd tools/zod-compare && node read-zorg.mjs
```

Then build again, because the tests read the fixtures that are copied to the output directory, and run the tests. `EmittedFixtureIsCurrent` fails while `zorg-emitted.json` is out of date.

To rewrite `RESULTS.md` after a final build:

```bash
ZORG_ZOD_WRITE=results dotnet test --project tests/Zorg.Json.ComplianceTests --no-build --filter-method "*ZodComparisonTests.WriteFixturesWhenAsked"
```

To list every disagreement with its classification, add `ZORG_ZOD_DIFFS=<file>`. It writes one JSON object per line, and `status` is `UNLISTED` for the ones that need an entry.

## Adding a case

Add the Zod schema to `catalogue.mjs` and the equivalent to `ZodCatalogue.cs` under the same name, then regenerate as above. Run the tests, and write an entry for each new disagreement, or fix what is wrong.
