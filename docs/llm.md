# LLM structured output

`Zorg.Json.Llm` adapts a schema to what OpenAI, Anthropic and Gemini accept, and parses the answer back. It's a separate package so it can be released whenever a provider changes its rules.

```csharp
static readonly ObjectSchema Answer = Z.Object()
    .Property("title", Z.String().MaxLength(80))
    .Property("score", Z.Integer().Minimum(0).Maximum(10))
    .Property("note", Z.String().Optional());

EmitResult emitted = Answer.Emit(LlmProfiles.OpenAIStrict);
JsonObject schema = emitted.Schema;                 // send this to the provider

ParseResult<JsonElement> answer = Answer.ParseModelOutput(emitted, modelJson);
```

Providers accept only part of JSON Schema. A profile sends a reduced schema to the model, and `ParseModelOutput` validates the answer against your **full** schema, so rules the provider can't enforce are still enforced. It also undoes lossy changes, such as turning `null` back into an absent property for what was optional.

| Profile | For |
|---|---|
| `LlmProfiles.OpenAIStrict` | OpenAI structured outputs in strict mode |
| `LlmProfiles.AnthropicStrict` | Anthropic structured outputs |
| `LlmProfiles.GeminiJsonSchema` | Gemini's JSON Schema subset |
| `LlmProfiles.GeminiOpenApi` | Gemini's OpenAPI-style `Schema` object |

## What a profile changes

- Objects are closed, and for OpenAI every property becomes required, with optional ones turned nullable.
- Keywords the provider rejects move into `description`, so the model still sees them.
- A root that isn't an object is wrapped in `{ "data": ... }` for OpenAI and Anthropic. `ParseModelOutput` unwraps it.
- Recursion the provider can't take is cut to `{}`, which is looser, and reported. Pass `Unrepresentable = Throw` to fail instead.

## Checking compatibility

```csharp
CompatibilityReport report = Answer.CheckCompatibility(LlmProfiles.OpenAIStrict);
```

The report lists what doesn't fit, with JSON Pointers, including count limits. Limits that apply per request can only be checked one schema at a time.

## Capability tables

Each profile uses a dated table such as `OpenAICapabilities.AsOf2026_11`. Provider rules change often, and old tables stay selectable through `LlmProfiles.CreateOpenAIStrict(table)`.

The tables come from provider documentation as of November 2026. They haven't been tested against live APIs, and in places the documentation is silent, so a few entries are assumptions on the safe side.

## Using a provider's SDK

- **Anthropic:** `emitted.Schema.ToJsonElementDictionary()` gives the shape its SDK takes.
- **Microsoft.Extensions.AI with OpenAI:** the adapter rewrites a supplied schema again, moving keywords such as `pattern` and `minimum` into `description`. `ParseModelOutput` still enforces them.
