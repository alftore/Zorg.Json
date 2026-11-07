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

Providers accept only part of JSON Schema. A profile sends a reduced schema to the model, and `ParseModelOutput` validates the answer against your **full** schema, so rules the provider can't enforce are still enforced. It also undoes lossy changes, such as turning `null` back into an absent property for what was optional, including inside the values of a map. Like `SafeParse`, it never throws for malformed output: a lone surrogate, invalid UTF-8, duplicate keys or very deep nesting give issues, and nesting below 64 levels is left as the model wrote it.

| Profile | For |
|---|---|
| `LlmProfiles.OpenAIStrict` | OpenAI structured outputs in strict mode |
| `LlmProfiles.AnthropicStrict` | Anthropic structured outputs |
| `LlmProfiles.GeminiJsonSchema` | Gemini's JSON Schema subset |
| `LlmProfiles.GeminiOpenApi` | Gemini's OpenAPI-style `Schema` object |

## What a profile changes

- Objects are closed, except in `GeminiOpenApi`, and for OpenAI every property becomes required, with optional ones turned nullable. A `Z.AllOf` of objects that only list properties becomes one object first, because closed objects reject each other's properties. If a branch has any other rule, a description or a title, you get a diagnostic: use `.Extend(other)`.
- Keywords the provider rejects move into `description`, so the model still sees them.
- A root that isn't an object is wrapped in `{ "data": ... }` for OpenAI and Anthropic. `ParseModelOutput` unwraps it.
- Recursion the provider can't take is cut to `{}`, which is looser, and reported. Pass `Unrepresentable = Throw` to fail instead. So is a definition that would be inlined more than a few thousand times, such as definitions that each use the next twice.
- References stay valid. A wrapped root moves `"$ref": "#"` to `#/properties/data`, `definitions` becomes `$defs` where the provider has no `definitions`, and `$anchor` references become pointers. A reference that can't be kept is reported as a `$ref` diagnostic, and `CheckCompatibility` lists it as an error of kind `Reference`.

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
- **Microsoft.Extensions.AI:** use `Zorg.Json.Extensions.AI`, described next.

## Microsoft.Extensions.AI

`Zorg.Json.Extensions.AI` depends on `Zorg.Json.Llm` and `Microsoft.Extensions.AI.Abstractions` 10.11.0, and nothing else. It works with Native AOT.

**A tool.** `ZorgAIFunction` is an `AIFunction`. The model sees the schema as `JsonSchema`. When it calls the function, the arguments are validated against the full schema first. If they're wrong, the function isn't called, and the model gets an error it can read, so it can try again:

```csharp
static readonly ObjectSchema Arguments = Z.Object()
    .Property("city", Z.String().MinLength(1))
    .Property("days", Z.Integer().Minimum(1).Maximum(14));

AIFunction forecast = ZorgAIFunction.Create("get_forecast", "Gets a weather forecast.", Arguments,
    arguments => $"Sunny in {arguments.GetProperty("city").GetString()}.");

ChatOptions options = new() { Tools = [forecast] };
```

```json
{
  "error": "invalid_arguments",
  "message": "The arguments don't match the schema. Fix them and call the function again.",
  "issues": [{ "path": "/days", "code": "too_big", "message": "..." }]
}
```

- At most 20 issues are listed, and `omitted` counts the rest.
- Arguments the model sent that can't be read as JSON, such as nesting that is too deep, get the same kind of error.
- An exception from your function, or from a `Refine` check, is not caught. It goes to the caller as it does from any `AIFunction`. `FunctionInvokingChatClient` then reports a failure to the model without the detail, unless you set `IncludeDetailedErrors`.
- The root of the schema must be an object, because providers take no other root for a tool.
- Pass a profile as the last argument, such as `LlmProfiles.OpenAIStrict`, to send what that provider takes. A `null` the model sends for an optional property is then read as absent, as `ParseModelOutput` does.
- `RefineAsync` checks run. Cancelling the token throws `OperationCanceledException`.
- A function that returns a `Task`, `Task<T>`, `ValueTask` or `ValueTask<T>` is awaited, and the model gets the result (null for the ones without a value). A task hidden in an `object` can't be awaited under Native AOT, so `Create` throws `InvalidOperationException` when it sees one.
- A .NET `string` argument with a lone surrogate is refused, like the same text from a model. A string nested inside an object or collection argument is written by System.Text.Json, which replaces a lone surrogate with U+FFFD, so the function sees the replacement.
- The schema is sent at any depth the emitter writes.

**A response format.**

```csharp
ChatOptions options = new() { ResponseFormat = Answer.ToChatResponseFormat(LlmProfiles.OpenAIStrict, "answer") };
```

An overload with `out EmitResult emitted` gives you the diagnostics, and the result to pass to `ParseModelOutput`.

**A typed answer.**

```csharp
ParseResult<JsonElement> answer = await chatClient.GetResponseAsync(Answer, "Rate this.", profile: LlmProfiles.OpenAIStrict);

if (answer.IsSuccess)
{
    Console.WriteLine(answer.Value);
}
```

This sets the response format on a copy of your `ChatOptions`, asks, and reads the text of the last assistant message that has any with `ParseModelOutputAsync`. Malformed, truncated, empty or non-JSON output, a lone surrogate, and a missing response are failed results with issues, never exceptions. An exception from the client, and cancellation, are exceptions. The usage and finish reason of the response aren't returned. If you need them, call the client yourself and pass `response.Text` to `ParseModelOutput`.

### The OpenAI adapter rewrites the schema again

The adapter in `Microsoft.Extensions.AI.OpenAI` (10.11.0) does not send a supplied schema as it is. It makes objects closed and every property required, which our OpenAI profile already did, so that part changes nothing. It also moves keywords such as `pattern`, `minimum` and `maximum` into `description`, even though the `OpenAIStrict` table lets them through. The provider is then not asked to hold those rules.

We checked this by sending schemas through the adapter to a fake HTTP handler, in `OpenAIAdapterTests`. A schema with nothing for the adapter to move reaches the wire unchanged, as the request body of a response format and as the parameters of a tool. A schema with rules keeps its properties, `required`, `additionalProperties` and nullable types, and the sent schema accepts everything the full schema accepts.

So with this adapter the rules the adapter moves are enforced only by us, when the answer is parsed. Use `GetResponseAsync(schema, ...)`, or `ParseModelOutput`, and don't read the model's text on its own. If you want the provider to hold `pattern` and the bounds, send the schema through the OpenAI SDK directly. We didn't see a `strict` flag in the request in these tests.
