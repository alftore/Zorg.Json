# ASP.NET Core

`Zorg.Json.AspNetCore` validates request bodies and serves schemas. `Zorg.Json.AspNetCore.OpenApi` adds the schemas to the OpenAPI document. It's a separate package because `Microsoft.AspNetCore.OpenApi` isn't in the shared framework.

## Validating request bodies

```csharp
app.MapPost("/people", (Validated<Person> person) => Results.Ok(person.Value))
    .ValidateBody(PersonSchema);
```

`Validated<T>` parses the body once and is the form to prefer. A plain parameter works too:

```csharp
app.MapPost("/people", (Person person) => Results.Ok(person)).ValidateBody(PersonSchema);
```

The framework's binder still runs for a plain parameter, so the validated value is serialized again for it to read. That costs a round trip per request, and under Native AOT `T` must be in the app's JSON options.

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

Request bodies use `Direction = Input` and responses use `Output`, both with `JsonSchemaProfile.OpenApi31`. Schemas marked `.Named("x")` become components, and response ones get an `Output.` prefix because the two directions can differ.

Reference `Microsoft.AspNetCore.OpenApi` directly from your app as well, because its MSBuild targets don't flow through this package. Under Native AOT the document needs `AddProblemDetails()`, because it describes the 400 response.

## Validating options

```csharp
builder.Services.AddOptions<SmtpOptions>()
    .ValidateWithSchema(SmtpSchema, AppJson.Default.SmtpOptions)
    .ValidateOnStart();
```
