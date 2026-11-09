# Known limits

These are known gaps and edge cases. Most of them need an unusual schema to show up, and each one is covered by a test that pins the current behaviour.

The two main promises hold everywhere except where this page says otherwise:

- The input schema never rejects what `SafeParse` accepts.
- `SafeParse` never throws, except from your own code.

Where the emitted schema can't say exactly what parsing does, it says less and reports a diagnostic, so it doesn't refuse valid data.

## Unions and combined schemas

- **Overlapping `Z.OneOf` of objects.** Objects are open by default, so two branches with only optional properties both match `{"a":1}`, and the result is `invalid_union`. Use `Z.DiscriminatedUnion`, or call `.Strict()` on the branches. For the same reason, the output schema of a `Z.OneOf` of objects can reject a result that matches both branches once stripped.
- **`Z.AllOf` branches see the same value, and their changes are merged.**
  - If one branch changes a value, say with `Trim` or `Coerce`, and another branch checks the same value, say with `MinLength`, the output schema can reject the merged result.
  - `MaxProperties` on a branch applies to that branch while parsing, so the output schema can reject the merged result.
  - A property and a `PatternProperty` that matches it are merged the same way, and have the same limits.
- **Deeply nested merges.** Merges are charged against the step limit. A `Z.AllOf` at every level of a nested object or a `Z.Lazy` tree costs depth squared. It fails with the step limit at about 18 levels for a binary tree and 26 for a chain, rather than running long.
- **Don't combine closed objects with `Z.AllOf`.** Use `.Extend(other)`. `Z.AllOf(a.Strict(), b.Strict())` rejects every value that has the other branch's properties.
- **`Z.If` whose branch changes the value.** If a branch strips properties or fills a default, `Validate` on the result can pick the other branch and reject the schema's own result. The output schema leaves out such an `if`.
- **`.DependentRequired("a", "b").Omit("b")` keeps `b`**, because the rule still names it.
- **`RefineAsync` inside `Not`, `If`, `Contains`, `AnyOf` or `OneOf`** counts as passed there. Put it on the outer schema.

## Typed schemas and the source generator

- Items of a collection aren't nullable in typed schemas, because the System.Text.Json contract doesn't say whether they are.
- A polymorphic base type that isn't abstract emits only its derived types, so JSON for the base type itself is refused.
- A get-only `IList<T>` property whose getter returns an array or a read-only collection is in the input schema. Parsing it fails with `deserialization_failed`, as the serializer does.
- `[RegularExpression]` is read as an ECMA-262 pattern, so `\d`, `\w`, `\s` and `.` match less than in .NET. The whole string has to match, but not necessarily through the first match .NET finds: for `a|ab`, the attribute rejects `ab` and Zorg accepts it.
- Suppose a pattern passes the generator's checks but isn't valid in Zorg's Unicode mode. It then throws when the schema is first built, and inside a `Schema.Lazy` that happens during parsing.
- A `Description` on a nested schema inside a cycle is lost.

## Importing JSON Schema

- Not supported: custom meta-schemas, `$vocabulary`, a change of dialect inside one document, and a `$ref` to an official meta-schema. They're refused at import with a stable error code.
- A user `$anchor` named `zorg-recursive` clashes with the name used for `$recursiveAnchor` in 2019-09.
- `$recursiveAnchor: false` in a resource in the middle: Zorg goes on to the outer resource, as jv does. JsonSchema.Net stops there instead. The test suite doesn't decide which is right.
- `unevaluatedProperties` and `unevaluatedItems` follow the static target of a `$dynamicRef` when deciding what the output schema can keep, so they can be too strict there.

## LLM profiles and `ParseModelOutput`

- **Unions.** `ParseModelOutput` parses against the open schema.
  - An overlapping `Z.OneOf` can fail with `invalid_union` even though the closed emitted schema told one branch apart. `.Strict()` on the branches avoids this.
  - It tries union branches without their refinements, so your code runs only once. A branch whose refinement then fails can win over a later branch that would have passed.
  - A node with both `anyOf` and `oneOf` has only its `anyOf` adapted.
- **Custom profiles.** A custom profile that keeps `not` or `if` and uses `CloseAllObjects` or `RequireAllAsNullable` also rewrites the objects inside them, which changes what they match. The built-in profiles move `not` and `if` into the description.
- **Missing diagnostics.**
  - A closing custom profile with `$ref` branches in an `allOf` gets no diagnostic.
  - Under OpenAI strict, an object `enum` or `const` on an object with optional properties is written with every property required, so no listed value can match. There's no diagnostic.
  - Gemini OpenAPI turns an unsatisfiable `{"type":"null","enum":[1]}` into `nullable` with that enum, which allows `1`. There's no diagnostic.
- **Gemini.**
  - It reads a `maximum` past 2^53, such as the one `ulong` states, as a double, so the bound there is approximate.
  - Its JSON Schema mode drops `minLength`, `maxLength` and the property counts without an error. Whether `responseSchema` enforces them isn't documented, so Zorg keeps them.

## ASP.NET Core and OpenAPI

- `ValidateBody` replays a whole decimal written with a fraction, such as `1.0`, as `1`, so a `decimal` loses its scale. `-0` reaches a `double` as `0`. The values are equal.
- `CheckCompatibility` checks the 2020-12 schema, which has no number formats, so it doesn't report the `format` diagnostics that `Emit(profile)` gives.
- **Microsoft.OpenApi output.** Some values can't be written correctly in an OpenAPI document. The document is then either looser than the schema or not valid JSON:
  - Inside `not` or a `oneOf` branch, a `const` or `enum` number past the range of `double`, or a `multipleOf` that a `decimal` can't hold, is kept, because dropping it there would make the schema stricter. Microsoft.OpenApi then writes `Infinity` or throws.
  - A number past the range of `double` inside an unknown keyword, an `x-inert-*` subtree or a `$comment` of an imported schema is written as `Infinity`.
  - `required: [""]` is written as `required: []`, and a U+0000 in a string `const` or `enum` is written as a raw byte.
- **OpenAPI 3.0.**
  - A `const` beside an `enum` is written as the enum alone, which is looser.
  - Unknown keywords of an imported schema are written as they are, where strict tooling expects only `x-` extensions. Validation is unaffected.
- **Component names.** Don't give a `Named` schema the name of a .NET type that an endpoint uses. If the named component comes first, the framework reuses it for that type. In the other order, building the document throws.
