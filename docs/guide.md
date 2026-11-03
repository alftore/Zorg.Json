# Guide

Zorg.Json turns one fluent definition into three things: a validator for raw JSON, a typed result, and a JSON Schema document.

## Two ways to define a schema

Use `Schema.*` when you have a .NET type, and `Z.*` when you don't.

```csharp
// Typed: bound to a record through its System.Text.Json contract.
public sealed record Person(string Name, string? Nickname, int Age);

[JsonSerializable(typeof(Person))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
public sealed partial class AppJson : JsonSerializerContext;

public static readonly ObjectSchema<Person> PersonSchema = Schema.Object(AppJson.Default.Person)
    .Property(p => p.Name, s => s.MinLength(1).MaxLength(100))
    .Property(p => p.Age, s => s.Minimum(0).Maximum(150));

// Untyped: for dynamic JSON such as tool arguments.
public static readonly ObjectSchema SearchSchema = Z.Object()
    .Property("query", Z.String().Trim().MinLength(1))
    .Property("limit", Z.Integer().Minimum(1).Maximum(50).Default(10))
    .Property("since", Z.String().Date().Optional());
```

Schemas are immutable and thread-safe. Every method returns a new schema, so store them in `static readonly` fields, and don't discard a result: `schema.MinLength(1);` on its own line does nothing.

## Parsing

```csharp
ParseResult<Person> result = PersonSchema.SafeParse(json);

if (result.IsSuccess)
{
    Person person = result.Value;
}
else
{
    foreach (ParseIssue issue in result.Issues)
    {
        Console.WriteLine($"{issue.InstanceLocation}: {issue.Code}: {issue.Message}");
    }
}
```

| Method | On invalid input |
|---|---|
| `SafeParse(json)` | Returns a failed `ParseResult<T>` |
| `TryParse(json, out value, out issues)` | Returns `false` |
| `Parse(json)` | Throws `SchemaValidationException` |
| `SafeParseAsync(stream)` | Returns a failed `ParseResult<T>`, and runs `RefineAsync` checks |

Input can be a `string`, UTF-8 bytes or a `JsonElement`. Malformed JSON and duplicate properties are issues too, not exceptions. Duplicates can't be detected in a `JsonElement` someone else parsed.

`ParseResult<T>.Map` and `Bind` convert a DTO to a domain object:

```csharp
ParseResult<Customer> customer = PersonSchema.SafeParse(json).Map(person => new Customer(person.Name));
```

## What a typed schema includes

`Schema.Object` starts from the type's contract, so a property you never mention is still validated. `.Property` only adds rules.

- **Names** come from the contract, so `[JsonPropertyName]` and naming policies apply.
- **Required:** a property marked `required`, or a constructor parameter with no default. System.Text.Json doesn't enforce the second kind itself, but `record Person(string Name)` says it's needed.
- **Nullable:** follows the declaration, so `string?` allows `null` and `string` doesn't. Absent and `null` are separate: `string? Nickname` as a constructor parameter must be present, and may be `null`. Use `.Optional(p => p.Nickname)` or give it a default to let it be absent.
- **Unknown properties** are rejected, unless the type has `[JsonExtensionData]`.
- **Computed properties** with no setter aren't accepted as input, but appear in the output schema.
- **`[JsonPolymorphic]` hierarchies** become a discriminated union. System.Text.Json expects the discriminator first unless `AllowOutOfOrderMetadataProperties` is set.

`Schema.Object<Person>()` is the short form using reflection, with web defaults. It doesn't work with trimming or Native AOT, which is why `Schema.Object(AppJson.Default.Person)` is the form to prefer.

A number bound to an `int` property rejects `1.0`, because System.Text.Json can't read it. `Z.Integer()` accepts `1.0`, as JSON Schema says.

## Changing values while parsing

Each value goes through a fixed pipeline:

**Default → Coerce → Normalize → type check → constraints → refinements**

```csharp
Z.Integer().Default(10)            // fills in an absent property
Z.Integer().Coerce()               // also accepts "42"
Z.Boolean().Coerce()               // also accepts "true" and "false"
Z.String().Trim().MinLength(1)     // "   " fails, because length is checked after trimming
Z.String().ToLowerInvariant().Enum("web", "news")
Z.String().Overwrite(text => text.Replace("-", ""))
```

There's no general `Transform`. Use `ParseResult<T>.Map` to convert to another type.

## Unknown properties

Objects reject unknown properties by default.

| Method | Unknown properties |
|---|---|
| `.Strict()` (default) | Fail with `unrecognized_keys` |
| `.Strip()` | Accepted and removed from the result |
| `.Passthrough()` | Accepted and kept |
| `.AdditionalProperties(schema)` | Validated against `schema` |

## Refinements

A refinement is a C# check that runs after the constraints pass.

```csharp
Z.String().Refine(name => name != "admin", "This name is reserved");

PersonSchema.Refine(p => p.Age >= 18 || p.Nickname is null, "Minors cannot have a nickname", p => p.Nickname);

Z.String().RefineAsync(users.IsFreeAsync, "This name is taken");   // needs SafeParseAsync
```

## Unions and recursion

```csharp
Z.AnyOf(Z.String(), Z.Integer());
Z.OneOf(Z.Integer(), Z.Number().Minimum(1));

// Selected by a property, with errors from the selected branch only.
Z.DiscriminatedUnion("kind",
    Z.Object().Property("kind", Z.String().Const("circle")).Property("radius", Z.Number()),
    Z.Object().Property("kind", Z.String().Const("square")).Property("side", Z.Number()));

// Typed, without [JsonPolymorphic]. The case schemas don't declare the discriminator.
Schema<Shape> shape = Schema.Union<Shape>("kind")
    .Case("circle", CircleSchema)
    .Case("square", SquareSchema);

// Recursive.
static readonly ObjectSchema Tree = Z.Object()
    .Property("value", Z.Integer())
    .Property("children", Z.Array(Z.Lazy("tree", () => Tree!)));
```

Combine objects with `.Extend(other)`, not `Z.AllOf`: a closed object rejects the properties of the other branch. `Extend` builds one flat object.

## Emitting JSON Schema

```csharp
JsonObject schema = PersonSchema.ToJsonSchema();
```

`ToJsonSchema()` describes the **input**: the JSON that parsing accepts. That's what a request body, a served schema or a tool's arguments need. Pass `Direction = SchemaDirection.Output` for what you send: responses and published events. There, a property with a `Default` is required, and normalized constraints apply.

| Option | Values |
|---|---|
| `Target` | `JsonSchemaProfile.Draft202012` (default), `Draft07`, `OpenApi30`, `OpenApi31` |
| `Direction` | `Input` (default), `Output` |
| `Unrepresentable` | `Annotate` (default), `Omit`, `Throw` |
| `Reused` | `Inline` (default), `Ref`, which writes schemas marked `.Named("x")` under `$defs` |
| `ReferencePrefix` | For example `#/components/schemas/`. Definitions then go in `EmitResult.Definitions` |

### The input guarantee

**The input schema accepts everything `SafeParse` accepts.** It's only ever looser, where JSON Schema can't express a rule:

- Refinements are C#.
- Constraints after `Trim` and similar steps. `Z.String().Trim().MaxLength(3)` accepts `"  abc  "`, so emitting `maxLength: 3` would wrongly reject it.
- Parts of a schema a dialect has no keyword for.

By default each of these is noted in `description`. `Emit()` also returns them as diagnostics:

```csharp
EmitResult result = Z.String().Trim().MaxLength(3).Emit();
JsonObject emitted = result.Schema;   // { "description": "After Trim: maxLength 3.", "type": "string", ... }
ImmutableArray<EmitDiagnostic> leftOut = result.Diagnostics;
```

The compliance tests check this guarantee against JsonSchema.Net, an independent validator.

## Issues

Every issue has a stable `Code`, a localized `Message`, a JSON Pointer `InstanceLocation` and usually a `Keyword`. `Parameters` holds details such as `minimum`.

| Code | Meaning | Keywords |
|---|---|---|
| `invalid_json` | Malformed JSON or a duplicate property | |
| `invalid_type` | Wrong JSON type, or doesn't fit the numeric type | `type` |
| `too_small` | Below a minimum | `minLength`, `minimum`, `exclusiveMinimum`, `minItems`, `minProperties`, `contains` |
| `too_big` | Above a maximum | `maxLength`, `maximum`, `exclusiveMaximum`, `maxItems`, `maxProperties`, `maxContains` |
| `invalid_format` | Doesn't match | `pattern`, `format` |
| `invalid_value` | Not an allowed value | `const`, `enum`, `not`, `uniqueItems` |
| `not_multiple_of` | Not a multiple | `multipleOf` |
| `required` | Missing property, reported at its own path | `required`, `dependentRequired` |
| `unrecognized_keys` | Unknown properties, listed in `Parameters["keys"]` | `additionalProperties` |
| `invalid_union` | No branch matched, or more than one of a `oneOf` | `anyOf`, `oneOf` |
| `deserialization_failed` | Valid, but System.Text.Json couldn't read it | |
| `custom` | A refinement failed | |

`issues.ToDictionary()` gives the shape of `ValidationProblemDetails`, and `issues.ToPrettyString()` is for logs.

## Strings are measured in code points

`MinLength` and `MaxLength` count Unicode code points, as JSON Schema says, so an emoji counts as one. Zod counts UTF-16 units.
