# Changelog

## Unreleased

**Breaking:** objects now accept unknown properties and remove them from the result, instead of rejecting them. This is `Z.Object()` and `Schema.Object(...)`. It matches `z.object()` in Zod and the default of System.Text.Json, and JSON Schema itself doesn't close objects unless asked. The input and output schemas no longer write `additionalProperties: false`. Call `.Strict()` on an object to keep the old behaviour. It applies to that object only. `JsonUnmappedMemberHandling.Disallow` closes a typed schema too, and with a `ReferenceHandler` other than `IgnoreCycles` unknown properties are kept, because `$id` and `$ref` belong to the serializer. Imported schemas are unchanged, and the LLM profiles that closed objects still do.

What that means in practice:

- `ValidateBody` in ASP.NET Core accepts a request with unknown fields and drops them, where it used to answer 400. The schemas in the OpenAPI document no longer write `additionalProperties: false`.
- A misspelled optional property is removed without an error. `.Strict()` reports it.
- A type with `[JsonExtensionData]` keeps unknown properties, as the serializer does.
- A property sent in another case, such as `Name` for `name` under the web defaults, is rejected with `unrecognized_keys`, as before, rather than removed, because the serializer would have bound it.
- A get-only property that the serializer fills, through `JsonObjectCreationHandling.Populate`, is read and checked like any other. That covers a `List<T>`, `HashSet<T>`, `Dictionary<TKey, TValue>` and their interfaces, and an object without constructor parameters, behind a getter that isn't nullable, in a class without them. The serializer ignores other get-only properties, so those are still refused.
- `ParseModelOutput` takes the first branch of a union whose emitted shape fits the answer and whose schema accepts it once adapted, so branches that differ only in their items or inside a nested object are told apart. Refinements run once, on the value the parse sees.
- The LLM profiles report an object beside a union of objects in a `Z.AllOf`, because closed objects there reject each other's properties.

Open objects don't exclude each other, so a `Z.OneOf` of objects can now reject what it accepted: two branches with only optional properties both match `{"a":1}`, which is `invalid_union`. Use `Z.DiscriminatedUnion`, or `.Strict()` on the branches. For the same reason such a `oneOf` can be written as `anyOf` when a branch isn't exact, and a `Z.AnyOf` returns what its first matching branch keeps.

- A property only written, such as a computed one, is refused when sent in, whatever the case options, so one the serializer would fill isn't lost.
- `Z.If` keeps the properties its condition declares when the condition holds. Only the names: what the condition changed while testing, such as a `Trim`, stays out of the result.
- `Z.If` keeps nested names its condition knows too, and an `if` whose condition changes the value is left out of the output schema with a diagnostic, as it already was from the input schema.
- A `Named` OpenAPI component that would share a name with the framework's component for a .NET type throws, instead of silently using the framework's. Numbers past the range of `double` are left out of `const`, `enum`, `default` and `examples` there, because Microsoft.OpenApi wrote them as `Infinity`.
- An `if` whose `then` or `else` changes the value is left out of the output schema, because the condition would be tested on the changed result.
- `ValidateBody` passes a whole number past the range of `long`, such as `1e19` for a `ulong`, to the binder as plain digits.
- The OpenAPI document leaves out a `multipleOf` that a `decimal` can't hold exactly, because Microsoft.OpenApi threw on it or rounded it.
- The type error for a number names its type for every numeric type, such as `uint64`, where it was empty.
- `byte`, `sbyte`, `short`, `ushort`, `uint` and `ulong` state their range with `minimum` and `maximum`, so a schema read without the type, such as by a model, keeps it. `decimal` writes `format: decimal` in OpenAPI, which the LLM profiles report.
- The output schema leaves out `not`, a `oneOf` that could overlap and `maxContains` where their subschemas change the value, as the input schema does, because they test the value as it came.
- A property that a `PatternProperty` also matches keeps what either schema keeps, as `allOf` branches do. The pattern's result used to replace the property's.
- The output schema leaves out `propertyNames` whose subschema changes the text, because names are never changed. `unevaluated*` is left out on output too where what evaluated beside it is.
- Merging what branches keep is charged for the bytes it compares and copies, so a deeply nested chain of `Z.AllOf` or pattern properties over a large document fails on the step limit instead of taking a minute.
- The output schema leaves out `contains` whose subschema changes the value, because `contains` doesn't change the items. It also leaves out an `if` whose branch changes names its condition notices: a stripped name where the condition reacts to extra names or tests a name it doesn't require, and a filled default where it tests names at all. A discriminator, whose tested names are required, keeps its `if`.
- A 3.0 OpenAPI document is emitted with the 3.0 profile and read as 3.0, so keywords beside a `$ref` survive inside `allOf`, a reference into a component's insides gets a component of its own, because 3.0 tools read a reference by its last segment, a format parsing doesn't check is inert, and what 3.0 lacks is left out without making a `not` or `oneOf` stricter. A 3.1 document moves `unevaluatedItems` to the description too, and both write only the tighter of two bounds on one side, because Microsoft.OpenApi kept the exclusive one.
- The Gemini OpenAPI profile moves a `format` other than `float`, `double`, `int32`, `int64`, `enum` and `date-time` to the description, with a diagnostic. Its `const` rewrite keeps an `enum` that is already there.
- `Int128`, `UInt128` and `Half` state their range, and typed schemas read them as numbers.
- `ValidateBody` no longer rounds a number a `decimal` can't hold exactly, such as `1e-29`, before the binder reads it.
- The OpenAPI document writes `const` as a one-value `enum` and boolean subschemas as `{}` and `{"not":{}}`, because Microsoft.OpenApi turned `3` into `"3"` and `false` into `null`.
- `Validate` reads computed properties inside a nullable named or polymorphic type and inside tuple items, so a type that disallows unknown properties accepts its own instances.
- The OpenAPI document no longer closes a tuple: Microsoft.OpenApi has no `prefixItems`, so they move to the description. A 3.0 document moves `patternProperties` there too, because the downgrade would otherwise refuse those names.
- `Z.AllOf` now merges what its branches rewrite, as Zod does, and a property stays if any branch keeps it. It used to keep the last rewrite, so objects that strip lost each other's properties.
- The LLM profiles turn a `Z.AllOf` of plain objects into one object before closing it. Anything else there gets a diagnostic, because closed objects reject each other's properties.
- An `enum` is written with each value once, since strict validators refuse one that repeats a value.
- `.Strip()` no longer writes `additionalProperties: false` to the output schema, and keeps a name that `DependentRequired` asks for.
- A case variant of a property is no longer taken by `AdditionalProperties` or `PatternProperty`, which let it bind past the rules of the property.
- A deep instance is no longer rejected under `$dynamicRef` or `$recursiveRef` beside `anyOf`, `oneOf` and `unevaluated*`. Finding what was evaluated doubled with the depth there and ran out of steps at about 13 levels.
- A reference copied out of its resource no longer keeps a `#name` that can't be found where it landed. A `$ref` is written as a pointer, and a `$dynamicRef` is left out with a diagnostic.
- An embedded `$schema` that isn't the target's is written as `x-inert-$schema`, so a bundled 2020-12 resource can be loaded as draft-07.
- A 2019-09 `$anchor` that 2020-12 doesn't allow, such as `a:b`, is written as a pointer.
- `contains` beside `unevaluatedItems` from a 2019-09 document now gets a diagnostic when it's written to 2020-12, where it's looser.
- Added a source generator, in the `Zorg.Json` package. `[GenerateSchema(typeof(AppJson))]` on a partial type writes a static `Schema` property from its contract and its validation attributes: `[MinLength]`, `[MaxLength]`, `[Length]`, `[StringLength]`, `[Range]`, `[RegularExpression]`, `[EmailAddress]`, `[Url]`, `[Required]` and `[Description]`. What it writes is ordinary fluent code, so it works under Native AOT. An attribute it can't state is a warning, `ZORG0005`, and it refuses polymorphic types and types with a custom converter, `ZORG0007`. See the guide for how each attribute reads.
- Added `JsonSchemaImportOptions.Documents`: other documents a `$ref` can reach, by URI. They're bundled into the schema as embedded resources, so nothing is fetched and the emitted schema needs nothing else.
- `$dynamicRef` and `$dynamicAnchor` now work. They were refused with `unsupported_keyword`. They're written to 2020-12 and OpenAPI 3.1, and left out with a diagnostic where they can't work: draft-07, OpenAPI 3.0, with `ReferencePrefix`, and with rewriters. They can't sit beside `$ref` in one schema object. A `$dynamicRef` that doesn't land on a `$dynamicAnchor` of its name is written as `$ref`, and inside a larger fluent schema dynamic keywords are left out, because the import shares a resource there. `SchemaNode` has new `DynamicRef` and `DynamicAnchor` properties.
- Added Draft 2019-09 import, including `$recursiveRef` and `$recursiveAnchor`, which are written as `$dynamicRef` and `$dynamicAnchor` when emitted.
- Refused cases of the official suite dropped from 88 to 9 for 2020-12, and to 4 each for draft-07, draft-06 and draft-04. Draft 2019-09 refuses 9 of 2,204. What remains is a `$ref` to an official meta-schema and custom meta-schemas.

## 0.1.0-preview.4

**Breaking:** an imported Draft 2020-12 schema no longer checks `format` unless you ask. The specification says that checking "MUST be disabled by default". Pass `Formats = FormatHandling.AssertKnown` to keep checking. Draft-04, draft-06, draft-07 and OpenAPI 3.0 still check, which those drafts allow, and fluent schemas are unchanged. `FormatHandling.ByDialect` is the new default.

- `unevaluatedProperties` and `unevaluatedItems` now work beside `$ref`, `allOf`, `anyOf`, `oneOf`, `if`, `dependentSchemas` and `contains`. These were refused with `unsupported_keyword`. A schema without them costs nothing extra. The emitter leaves them out with a diagnostic when an `if` beside them can't be written, because the written schema would otherwise be stricter than parsing.
- A `$ref` can now reach any `$id` in the same document by URI: relative, absolute or URN, with a pointer or an anchor. Bundled documents import. Nothing is fetched, and what isn't in the document stays `unresolved_ref`. Refused cases of the official suite dropped from 252 to 88 for 2020-12, from 63 to 27 for draft-07, from 55 to 27 for draft-06 and from 32 to 21 for draft-04.
- With `ReferencePrefix`, imported schemas no longer write `$id` or `$anchor`, because a pointer in a component reads from the whole document. A schema on two endpoints no longer repeats them, and two definitions that differ only in `#z` against `#/$defs/t` no longer throw. A root with an `$id` now moves its definitions to `EmitResult.Definitions` like any other.
- An imported `format` that parsing doesn't check is written as `x-inert-format` to draft-07 and OpenAPI 3.0, with a diagnostic, because validators of those dialects may check it and reject what parsing accepts. Import with `Formats = FormatHandling.AssertKnown` to keep `format` there. 2020-12 and OpenAPI 3.1 output keep `format`.
- Two resources with the same URI in one document are now refused with `invalid_schema`.
- A draft-07 `$id` that spells its place, such as `#/definitions/a`, is now read. It used to be refused.
- Fixed `"$id":"http://x/y#"` beside an anchor being written to draft-07 as `http://x/y##a`.
- Ids are compared as URIs when they're written, so two nested ids with the same text under different bases keep it. When two imports in one fluent schema share an id, the second id changes and references to it follow.
- `RelocateToDescription` takes `unevaluated*` along when anything beside it applies to the same value, `additionalProperties` along with `patternProperties`, `items` along with `prefixItems`, and `contains` along with `minContains: 0`, which could make the written schema stricter than parsing.
- `InlineOrRejectRecursion` keeps what sits beside a `$ref`, in an `allOf`. A reference it can't inline now takes `not`, `if`, `oneOf`, `contains` with `maxContains`, and `unevaluated*` with it where it sits under them, because an open schema there is stricter, not looser.
- A thread with a small stack now fails the parse instead of ending the process.
- A draft-07 `$id` with a fragment on the URI around it names a schema, and starts no new resource.
- Each property now counts towards the evaluation limits, so a wide object under shared subschemas stops within them. It used to take minutes.
- The tests now also compare Zorg with a second independent validator, santhosh-tekuri/jsonschema, on generated documents. Set `ZORG_JV` to the path of its `jv` tool to run them. CI does.
- Fixed an imported `oneOf` of objects being written as `anyOf` when a branch held a `format`, though the branches exclude each other, as in a discriminated union or closed objects with different required properties. It now stays `oneOf` when each pair of branches shares no type, or shares only `object` and one requires a property the other forbids or limits to other values. These schemas round trip, and `UnrepresentablePolicy.Throw` doesn't throw on them. Only `required`, `properties`, `additionalProperties`, `const` and `enum` count, because every validator applies those. Branches without a `type` still become `anyOf`, since a string would match both.

## 0.1.0-preview.3

- Added the package `Zorg.Json.Extensions.AI`, for Microsoft.Extensions.AI 10.11.0:
  - `ZorgAIFunction` is an `AIFunction` whose schema comes from a Zorg schema. It validates the model's arguments and returns a structured error to the model when they're wrong, instead of throwing.
  - `ToChatResponseFormat` makes a `ChatResponseFormat` from a schema and an optional profile.
  - `chatClient.GetResponseAsync(schema, ...)` asks for JSON and returns a `ParseResult<T>`. Malformed, truncated or empty output is a failed result.
  - It's compatible with Native AOT.
- Added `ParseModelOutputAsync` to `Zorg.Json.Llm`, which runs `RefineAsync` checks.
- Fixed an imported `oneOf` being written as `anyOf` when a branch held a `format`, though its branches can't overlap, as in a nullable `$ref`. Branches of different types now keep `oneOf`, so these schemas round trip again and `UnrepresentablePolicy.Throw` doesn't throw on them. Branches that can overlap once `format` is ignored still become `anyOf`.
- The diagnostic for that `anyOf` now says why it's needed.
- Diagnostics for a relaxed `oneOf`, and for a left-out `not`, `if` or `maxContains`, now name the rule that caused it and where it is, such as `format at /not/properties/id`. The `UnrepresentablePolicy.Throw` message says the same. Verdicts are unchanged.
- Added `Z.Enum<TEnum>()` for C# enums. It emits `enum` with the member names and parses to `TEnum`. Names are case-sensitive, numbers are refused, and `[JsonStringEnumMemberName]` and an optional `JsonNamingPolicy` set the names. It works under Native AOT.
- Added string shortcuts for formats Zorg already enforces: `Time`, `Duration`, `Hostname`, `IdnHostname`, `IdnEmail`, `Ipv4`, `Ipv6`, `Iri`, `IriReference`, `UriReference`, `UriTemplate`, `JsonPointer` and `RelativeJsonPointer`.
- `Meta` refuses, with an `ArgumentException`, the name of any JSON Schema or OpenAPI 3.0 keyword, such as `type` or `enum`. The emitter writes those itself, and a custom value replaced what it wrote, so the input schema could reject what `SafeParse` accepts. `x-` names and other custom names still work, and importing keeps unknown keywords as before.
- Fixed an emitted OpenAPI 3.0 `oneOf` that relaxed to `anyOf` in a branch with several types, which dropped the type limit. The relaxed list now sits in `allOf`.
- OpenAPI 3.0: a null-typed schema with `const` or `enum` is no longer taken for exact, because it is written without a type. `not` and `oneOf` around it are relaxed or left out with a diagnostic.
- Naming the rule that relaxed a keyword no longer costs much more than judging exactness: the search keeps parent links instead of copying paths, skips what is known to be exact, and past 10,000 subschemas (100,000 in all) names only the keyword. Long paths in a diagnostic are shortened.
- `Zorg.Json.Extensions.AI`: `ZorgAIFunction.Create` awaits functions that return `Task`, `Task<T>`, `ValueTask` or `ValueTask<T>`. A schema nested more than 31 objects deep no longer throws. Only assistant messages count as the answer. A .NET `string` argument with a lone surrogate is refused.
- `ParseModelOutput` and `ParseModelOutputAsync` return a failed result for a default `JsonElement` instead of throwing.
- Fixed an import that overflowed the stack and ended the process: a draft-07 document with a reference to the root of a resource outside the keywords Zorg walks.
- Emitted references are now written last, from where each target ended up, in place of rewriting their text. This closes the three limits `docs/import.md` listed: a `$ref` into an `allOf` beside a wrapped `$ref`, a root with an `$id` under `RequireObjectRoot`, and `ReferencePrefix` leaving references dangling.
- With `ReferencePrefix`, a target outside the definitions, the root included, now goes to `EmitResult.Definitions`, and the definitions of an imported schema there are named with a hash of their content and all they reach (`a-3fa9c21e7d10b6e4`), so schemas sharing one OpenAPI document don't clash and identical definitions share a component, whatever the order of the endpoints. This changes those names: `a` is now `a-3fa9c21e7d10b6e4`. Names from `.Named` don't change, and a clash between them still throws. A boolean definition is no longer lost there.
- Fixed two `.Named` imports with a `$ref` at the root sharing a target, which could make the input schema stricter than parsing.
- The LLM profiles resolve an anchor in the resource of its reference, and start a pointer inside a resource at the document when the provider has no `$id`.
- An import used twice no longer repeats its `$id` or `$anchor`. The second copy gets an `$id` with a suffix.
- An anchor on a schema the dialect leaves out no longer dangles, and OpenAPI 3.0 output of an imported schema has no `$id` or `$anchor`.
- A `$ref` into an unknown keyword now gets a home under `$defs` in every dialect, as it did before 2020-12, because references inside that text can't be moved with it. `#/unknown-keyword` becomes `#/$defs/target-1`.

## 0.1.0-preview.2

Known limit: inside `Not`, `If`, `Contains`, `AnyOf` or `OneOf`, a `RefineAsync` check counts as passed, because these decide before async checks run. `Contains` can accept a value the check would refuse, and `AnyOf` can reject a value another branch would accept. Put `RefineAsync` on the outer schema instead. See the guide.

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
- Evaluation fixes:
  - Instance paths copied into the issues of speculative branches are charged to a budget, so a long property name with a long array no longer takes quadratic time.
  - `SafeParse(JsonElement)` no longer throws on bytes that aren't UTF-8 when it rewrites an object or array, or reads a discriminator.
  - `const`, `enum` and a discriminated union fail the parse on a value with an unreadable string, so `Z.Not(...)`, `Z.If(...)` and `Z.AnyOf(...)` over them can't accept `"\ud800"`.
  - A discriminator compares values as JSON, so `1` and `1.0` select the same branch, and branches with equal values make it a plain `oneOf`.
  - Numbers are compared as digits and an exponent, in linear time, and long numbers are charged to the step budget. Before, a ten-megabyte number took seconds to parse. Exponents are exact up to 18 digits (`1e2000000000` and `1e3000000000` used to be equal), and a longer one fails the parse where a schema has to compare it.
  - Failed `anyOf` and `oneOf` branches keep at most 20,000 issues over a parse, so a recursive union over a large document no longer holds gigabytes.
  - An exception thrown by your own `Refine` or `RefineAsync`, including a `JsonException`, passes through instead of becoming `deserialization_failed`.
  - The stream overload refuses a UTF-8 byte order mark as `invalid_json`, as the string and byte overloads do. It used to skip it. A bad byte and the escape `\udcff` are different strings for `uniqueItems`.
- More evaluation fixes:
  - A number with an exponent of more than 18 digits can't be hashed to find an equal number, so `uniqueItems` (with two items or more), a large `enum` and a discriminated union now fail the parse when they hold or are given one, where before `[1e999999999999999999, 0.1e1000000000000000000]` passed `uniqueItems` and `Z.Not(...)` over a large `enum` accepted such a number.
  - `multipleOf` charges for raising ten to a long exponent modulo the divisor. A 1,001-digit divisor under `not` used to take seconds over a 105 KB array and now fails the parse with the limit.
  - `GenerateSample` no longer ends the process on a schema nested 100,000 levels deep.
  - A number beyond the range of the hash is no longer compared with every value of an `enum` that holds no numbers, and the comparison is charged where it holds some. Reading a divisor that has as many digits as the number it divides is charged to `multipleOf` as well.
- More emission fixes:
  - An imported schema with an `$id` of its own is a resource again when it sits in another schema: its `$defs` and references are left as they are, instead of being moved to the document, which left the references dangling. A reference inside such a resource to a place the dialect doesn't write (`#/if` in OpenAPI 3.0) is written again under the definitions of that resource.
  - A moved target is named `target-1` and so on, but never after a definition the document has, which it used to replace.
  - Pointing a reference at a moved target reads only schemas, so a `const`, `enum`, `default` or example that spells `{"$ref": ...}` is left alone.
  - A draft-07 dependency written as a list and a schema together is one value, so a reference to its schema alone is written again instead of pointing at both.
  - A `definitions` keyword and a `$defs` keyword with one name no longer share a pointer in draft-07.
  - `RelocateToDescription` follows `$ref` and `Z.Lazy` when it decides whether a `not`, `if`, `oneOf` or `contains` with `maxContains` holds a keyword it moves, so a definition used under `not` is not loosened.
  - The search for a keyword to move, and the one for a subschema that isn't exact, stop at 100,000 subschemas and throw `InvalidOperationException`, where a `Z.Lazy` that builds a new schema each time it is read never ended. The first also remembers what it found, so a schema used by thousands of definitions is walked once.
  - OpenAPI 3.0 has no `$id`, so an import with one that sits in another schema loses it there and its references and `$defs` are moved like any other. A copy of a draft-07 dependency that was written as a list and a schema together gets an `$id` of its own, so the document has no duplicate and the references in the copy still read from it.
  - Known limits, listed in the import guide: references into the `allOf` of a schema with a `$ref` in draft-07, a root with an `$id` moved into another schema's properties, and references to unwritten places with `ReferencePrefix`.
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
