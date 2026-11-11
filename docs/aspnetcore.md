# ASP.NET Core

`Zorg.Json.AspNetCore` validates request bodies and serves schemas. `Zorg.Json.AspNetCore.OpenApi` adds the schemas to the OpenAPI document. It's a separate package because `Microsoft.AspNetCore.OpenApi` isn't in the shared framework.

## Validating request bodies

```csharp
app.MapPost("/people", (Validated<Person> person) => Results.Ok(person.Value))
    .ValidateBody(PersonSchema);
```

`Validated<T>` parses the body once and is the form to prefer. Don't add another body parameter, such as `[FromBody] Person`, beside it: the body is read once, so that one gets nothing and the request fails with an empty 400. A plain parameter instead of `Validated<T>` works too:

```csharp
app.MapPost("/people", (Person person) => Results.Ok(person)).ValidateBody(PersonSchema);
```

The framework's binder still runs for a plain parameter, so the validated JSON, with defaults, `Trim`, `Coerce` and `Strip` applied, is replayed for it to read. It's the JSON itself rather than a serialized `T`, so an absent `Optional<T>` stays absent and a PATCH that omits a field doesn't clear it. Under Native AOT `T` must be in the app's JSON options.

The body must be UTF-8, which is what validation reads and what RFC 8259 requires. A `charset` other than `utf-8` gets a 415 problem, and the replayed request is given a plain `application/json` content type so the binder can't decode it differently.

Validation wraps the endpoint rather than being an endpoint filter, because filters don't run when body binding fails, so they never see malformed JSON. Your own filters still run, after validation.

An invalid body gets a 400 with RFC 9457 problem details. `errors` and `codes` are both keyed by JSON Pointer, in the same order:

```json
{
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": { "/age": ["Must be at least 0."] },
  "codes": { "/age": ["too_small"] }
}
```

At most 100 issues are written. If there were more, a `too_many_issues` entry under `""` says so. Pointers and messages are cut at 256 characters, because they can echo what the client sent.

Malformed JSON is also a 400, with `invalid_json` under the key `""`. `issues.ToProblemDetails()` builds the same response yourself.

## Serving schemas

```csharp
app.MapJsonSchema("/schemas/person.json", PersonSchema);
```

It answers GET and HEAD with `application/schema+json`. The schema is emitted once, and `ETag` and `If-None-Match` work. Pass `JsonSchemaOptions` to choose the target or direction.

## OpenAPI

```csharp
builder.Services.AddOpenApi(options => options.AddEndpointSchemas());

app.MapPost("/people", (Validated<Person> person) => Results.Ok(person.Value))
    .ValidateBody(PersonSchema)
    .ProducesSchema(PersonSchema);
```

Request bodies use `Direction = Input` and responses use `Output`, with `JsonSchemaProfile.OpenApi31`, or `OpenApi30` when the document is OpenAPI 3.0. An OpenAPI 3.2 document, the default of ASP.NET Core 11, uses the 3.1 profile, because both versions use JSON Schema 2020-12. The `$defs` of an imported schema become components too, named `a-3fa9c21e7d10b6e4`: the hash covers the definition and all it reaches, so endpoints with the same definition share a component and different ones never clash, in any order. Schemas marked `.Named("x")` become components, and response ones get an `Output.` prefix because the two directions can differ. Two different schemas with the same name in one document throw an `InvalidOperationException` that names the clash, rather than silently keeping the first. The names share the document with the framework's components for .NET types, so don't name a schema after a type an endpoint takes or returns: when the framework's component comes first it throws, and when Zorg's comes first the framework's references point at it. A 3.0 document gets what the 3.0 profile writes: keywords beside a `$ref` move into `allOf`, a known format parsing doesn't check is kept as `x-inert-format` (tools ignore one they don't know), and what 3.0 can't state is left out. `Microsoft.OpenApi` can't hold some 3.1 keywords, so a 3.1 document moves `prefixItems` and `unevaluatedItems` to the description, and both write `const` as a one-value `enum`, boolean subschemas as objects, and only the tighter of two bounds on one side. Where nothing negates it, a number past the range of `double` in `const` or `enum`, which Microsoft.OpenApi would write as `Infinity`, and a `multipleOf` a `decimal` can't hold exactly are left out, and so are such numbers in `default` and `examples`. Where a keyword is moved under `not` or in a `oneOf` branch, the whole applicator moves, so the document stays looser than parsing.

Reference `Microsoft.AspNetCore.OpenApi` directly from your app as well, because its MSBuild targets don't flow through this package. The package has a build for .NET 10 and one for .NET 11, because version 11 of `Microsoft.AspNetCore.OpenApi` moved to `Microsoft.OpenApi` 3.x, which isn't compatible with the 2.x of version 10. An app on .NET 10 gets the build for version 10, and an app on .NET 11 the build for version 11, so on .NET 11 reference version 11 or later. Under Native AOT the document needs `AddProblemDetails()`, because it describes the 400 response.

## Validating options

```csharp
builder.Services.AddOptions<SmtpOptions>()
    .ValidateWithSchema(SmtpSchema, AppJson.Default.SmtpOptions)
    .ValidateOnStart();
```
