# .NET versions

For maintainers: what each .NET release means for Zorg.Json, so it can be revisited when a version arrives or leaves support. Packages target the oldest supported LTS, plus newer targets only where a newer API is needed, behind `#if NET<N>_0_OR_GREATER`.

## .NET 10 (LTS, Nov 2025 – Nov 2028)

The baseline: every package targets `net10.0`.

- **Relies on:** `JsonElement.Parse`, `JsonTypeInfo` contracts, `JsonObjectCreationHandling.Populate`, `Int128`/`UInt128`/`Half`, source-generated regexes, Native AOT.
- **When it leaves support:** move the baseline to .NET 12, and delete the `#if NET11_0_OR_GREATER` branches and the `net10.0` builds of `Zorg.Json` and `Zorg.Json.AspNetCore.OpenApi`.

## .NET 11 (STS, Nov 2026 – Nov 2028)

`Zorg.Json` and `Zorg.Json.AspNetCore.OpenApi` also target `net11.0`. The other packages run on it unchanged.

- **Used:**
  - C# 15 unions (`JsonTypeInfoKind.Union`, `UnionCases`) become `anyOf` of the cases and `null`, and nothing inside them is removed, so the serializer's classifier sees the JSON as written.
  - `BFloat16`, `Decimal32`, `Decimal64` and `Decimal128` are numbers, with no stated range: the serializer writes `BFloat16.MaxValue` as `3.39E+38`, past its exact value.
  - `StringBuilder.Append(Rune)`.
  - `Microsoft.AspNetCore.OpenApi` 11 uses `Microsoft.OpenApi` 3.x, which isn't binary compatible with the 2.x of version 10, so the OpenAPI package needs its own build. OpenAPI 3.2 documents go through the 3.1 path.
- **Reported, not used:** `AsyncValidationAttribute` and `IAsyncValidatableObject` get warning ZORG0005, because they need services from their `ValidationContext`.
- **Already works through the contract:** `closed` hierarchies with `InferClosedTypePolymorphism`, `IReadOnlySet<T>`, extension data in an `IReadOnlyDictionary` or a `JsonObject`, `[JsonNamingPolicy]` on a member, `JsonNamingPolicy.PascalCase`.
- **Not used yet, worth a look once it's the baseline:**
  - `string.StartsWith(char, StringComparison)` and its siblings, and `GetTypeInfo<T>()`. CA1307 and CA2263 are off for `net11.0` only because of these.
  - `Utf8.IndexOfInvalidSubsequence` and `Utf16.IsValid`, which could replace our own lone-surrogate scans.
  - `JsonSerializer.SerializeAsyncEnumerable(topLevelValues: true)`, which writes JSON Lines and could back parsing JSON Lines input.
  - Union classifiers: a diagnostic when cases are ambiguous to the default classifier.
- **Not relevant:** `RegexOptions.AnyNewLine` (our patterns are ECMA-262), and runtime async (still a preview).

## .NET 12 (LTS, expected Nov 2027)

- **When it ships:** check System.Text.Json, DataAnnotations and `Microsoft.AspNetCore.OpenApi` for new contract kinds, number types and breaking changes. Run the full test suite on the new runtime with `DOTNET_ROLL_FORWARD=LatestMajor` before adding a target.
- **In Nov 2028,** when .NET 10 and 11 leave support, it becomes the baseline. See .NET 10 above.
