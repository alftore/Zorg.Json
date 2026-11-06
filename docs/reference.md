# Keyword and Zod reference

Method names follow JSON Schema keywords first. Coming from Zod, look up the name here.

## Types

| JSON Schema | Zorg.Json | Zod |
|---|---|---|
| `type: string` | `Z.String()` | `z.string()` |
| `type: number` | `Z.Number()`, `Z.Number<decimal>()` | `z.number()` |
| `type: integer` | `Z.Integer()`, `Z.Number<int>()` | `z.int()`, `z.int32()` |
| `type: boolean` | `Z.Boolean()` | `z.boolean()` |
| `type: string`, `enum` of member names | `Z.Enum<TEnum>()` | `z.enum()` |
| `type: null` | `Z.Null()` | `z.null()` |
| `{}` | `Z.Any()` | `z.unknown()` |
| `false` | `Z.Never()` | `z.never()` |
| `type: array`, `items` | `Z.Array(items)` | `z.array()` |
| `prefixItems` | `Z.Tuple(a, b)` | `z.tuple()` |
| `type: object` | `Z.Object()`, `Schema.Object(typeInfo)` | `z.object()` |
| `additionalProperties: schema` | `Z.Record(values)` | `z.record()` |
| `format` with a typed result | `Schema.Parsable<Guid>("uuid")` | `z.uuid()` |

## Strings

| JSON Schema | Zorg.Json | Zod |
|---|---|---|
| `minLength` | `.MinLength(n)` | `.min(n)` |
| `maxLength` | `.MaxLength(n)` | `.max(n)` |
| `pattern` | `.Pattern(text)`, `.Pattern(regex, ecmaPattern)` | `.regex()` |
| `format` | `.Format(name)` | |
| `format: email` | `.Email()` | `z.email()` |
| `format: uuid` | `.Uuid()` | `z.uuid()` |
| `format: uri` | `.Uri()` | `z.url()` |
| `format: date-time` | `.DateTime()` | `z.iso.datetime()` |
| `format: date` | `.Date()` | `z.iso.date()` |
| `format: time` | `.Time()` | `z.iso.time()` |
| `format: duration` | `.Duration()` | `z.iso.duration()` |
| `format: hostname` | `.Hostname()` | `z.hostname()` |
| `format: ipv4` | `.Ipv4()` | `z.ipv4()` |
| `format: ipv6` | `.Ipv6()` | `z.ipv6()` |
| `format: idn-email` | `.IdnEmail()` | |
| `format: idn-hostname` | `.IdnHostname()` | |
| `format: iri` | `.Iri()` | |
| `format: iri-reference` | `.IriReference()` | |
| `format: uri-reference` | `.UriReference()` | |
| `format: uri-template` | `.UriTemplate()` | |
| `format: json-pointer` | `.JsonPointer()` | |
| `format: relative-json-pointer` | `.RelativeJsonPointer()` | |
| `const` | `.Const(value)` | `z.literal()` |
| `enum` | `.Enum(a, b)` | `z.enum()` |
| | `.Trim()`, `.TrimStart()`, `.TrimEnd()` | `.trim()` |
| | `.ToLowerInvariant()`, `.ToUpperInvariant()` | `.toLowerCase()`, `.toUpperCase()` |
| | `.NormalizeUnicode(form)` | `.normalize()` |
| | `.Overwrite(function)` | `.overwrite()` |

## Numbers

| JSON Schema | Zorg.Json | Zod |
|---|---|---|
| `minimum` | `.Minimum(n)` | `.gte(n)` |
| `maximum` | `.Maximum(n)` | `.lte(n)` |
| `exclusiveMinimum` | `.ExclusiveMinimum(n)`, `.Positive()` | `.gt(n)`, `.positive()` |
| `exclusiveMaximum` | `.ExclusiveMaximum(n)`, `.Negative()` | `.lt(n)`, `.negative()` |
| `multipleOf` | `.MultipleOf(n)` | `.multipleOf(n)` |
| `const`, `enum` | `.Const(n)`, `.Enum(a, b)` | `z.literal()` |
| `type: [string, integer]` on input | `.Coerce()` | `z.coerce.number()` |

Booleans have `.Const(value)` and `.Coerce()`.

## Arrays

| JSON Schema | Zorg.Json | Zod |
|---|---|---|
| `minItems` | `.MinItems(n)`, `.NonEmpty()` | `.min(n)`, `.nonempty()` |
| `maxItems` | `.MaxItems(n)` | `.max(n)` |
| `uniqueItems` | `.UniqueItems()` | |
| `items` | `.Items(schema)` | |
| `contains`, `minContains`, `maxContains` | `.Contains(schema, minimum, maximum)` | |

## Objects

| JSON Schema | Zorg.Json | Zod |
|---|---|---|
| `properties`, `required` | `.Property("name", schema)` | a key of `z.object()` |
| `additionalProperties: false` | `.Strict()` | `.strict()` |
| no `additionalProperties` | `.Strip()` (the default) | `.strip()` (the default) |
| no `additionalProperties` | `.Passthrough()` | `.passthrough()` |
| `additionalProperties: schema` | `.AdditionalProperties(schema)` | `.catchall()` |
| `patternProperties` | `.PatternProperty(pattern, schema)` | |
| `propertyNames` | `.PropertyNames(schema)` | |
| `minProperties`, `maxProperties` | `.MinProperties(n)`, `.MaxProperties(n)` | |
| `dependentRequired` | `.DependentRequired(name, others)` | |
| | `.Pick(names)`, `.Omit(names)`, `.Partial()`, `.Extend(other)` | same names |

`ObjectSchema<T>` adds `.Property(p => p.Name, s => ...)`, `.Property(p => p.Home, schema)`, `.Optional(p => p.X)`, `.Omit(p => p.X)` and `.Refine(predicate, message, p => p.X)`. Both `Property` forms also take an `Optional<T>` property.

| JSON Schema | Zorg.Json | Zod |
|---|---|---|
| not in `required` | `Optional<T>` property, `[JsonConverter(typeof(OptionalJsonConverter<T>))]` | `.optional()`, which also separates absent from `null` |

## Composition

| JSON Schema | Zorg.Json | Zod |
|---|---|---|
| `anyOf` | `Z.AnyOf(a, b)` | `z.union()` |
| `oneOf` | `Z.OneOf(a, b)` | `z.xor()` |
| `oneOf` with `const` | `Z.DiscriminatedUnion(name, a, b)`, `Schema.Union<T>(name)` | `z.discriminatedUnion()` |
| `allOf` | `Z.AllOf(a, b)` | `z.intersection()` |
| `not` | `Z.Not(schema)` | |
| `if`, `then`, `else` | `Z.If(condition, then, otherwise)` | |
| `$ref` | `Z.Lazy(name, getter)`, `Schema.Lazy(name, getter)` | `z.lazy()` |

## Every schema

| JSON Schema | Zorg.Json | Zod |
|---|---|---|
| `title` | `.Title(text)` | `.meta({ title })` |
| `description` | `.Description(text)`, `.Describe(text)` | `.describe()` |
| `$comment` | `.Comment(text)` | |
| `examples` | `.Examples(a, b)` | `.meta({ examples })` |
| `deprecated`, `readOnly`, `writeOnly` | `.Deprecated()`, `.ReadOnly()`, `.WriteOnly()` | `.meta()` |
| `x-*` | `.Meta(keyword, value)`, which refuses the name of a JSON Schema keyword | `.meta()` |
| `default` | `.Default(value)` | `.default()` |
| not in `required` | `.Optional()` | `.optional()` |
| adds `null` to `type` | `.Nullable()` | `.nullable()` |
| | `.Refine(predicate, message)`, `.RefineAsync(...)` | `.refine()` |
| `$defs` | `.Named(name)` with `Reused = Ref` | `.meta({ id })` |

## Parsing and emitting

| Zorg.Json | Zod |
|---|---|
| `schema.SafeParse(json)` | `schema.safeParse(value)` |
| `schema.SafeParseAsync(stream)` | `schema.safeParseAsync(value)` |
| `schema.Validate(instance)`, `ValidateAsync` | `schema.safeParse(value)` on a value you hold |
| `schema.Parse(json)` | `schema.parse(value)` |
| `result.Value`, `result.Issues` | `result.data`, `result.error.issues` |
| `issues.ToDictionary()` | `z.flattenError()` |
| `issues.ToPrettyString()` | `z.prettifyError()` |
| `schema.ToJsonSchema()` | `z.toJSONSchema(schema)` |
| `Direction = Input` (default) | `io: "input"` (Zod's default is output) |
| `Target = JsonSchemaProfile.Draft07` | `target: "draft-07"` |
| `Unrepresentable = Throw` | `unrepresentable: "throw"` |
| `Unrepresentable = Callback`, `UnrepresentableHandler` | none. The closest is a custom `override` on `z.toJSONSchema` |
| `schema.GenerateSample()` (`Zorg.Json.Sampling`) | none. NJsonSchema: `SampleJsonDataGenerator` |

## Differences from Zod

Known limit: inside `Not`, `If`, `Contains`, `AnyOf` or `OneOf`, a `RefineAsync` check counts as passed, because these decide before async checks run. Zod awaits it. Put `RefineAsync` on the outer schema instead.

Zorg.Json follows JSON Schema where Zod follows JavaScript, so the same schema can give a different verdict, a different issue or a different value. Each difference below was found by running both libraries over the same schemas and documents (see [tools/zod-compare](../tools/zod-compare/README.md)), and each is on purpose.

### Issue codes

Verdicts agree here. The issues differ, so don't match on Zod's codes.

- **Literals and enums.** Zod reports `invalid_value` for any input that isn't the literal or an enum member. Zorg reports `invalid_type` first when the JSON type is already wrong, and `invalid_value` (`const` or `enum`) when the type is right.
- **`Z.Never()`.** It is the JSON Schema `false` schema, so it reports `invalid_value`. Zod reports `invalid_type`.
- **Tuples.** Items beyond a tuple are one `too_big` at the root in Zod. Zorg reports each as `invalid_value` at the item (`items: false`), and also the type errors of the items that are present. For a short tuple Zod reports only `too_small`, and Zorg adds those type errors as well.
- **Unique items.** `UniqueItems` reports `invalid_value` with the keyword `uniqueItems` and the two indexes, and still checks items that have type errors. A Zod `refine` reports `custom` and runs only on valid items.
- **Record keys.** A key that fails the key schema is `invalid_key` at the key in Zod. In Zorg it is a `propertyNames` failure, `invalid_format`, at the object.
- **Unions.** When no branch matches, Zorg reports one `invalid_union` that holds the issues of every branch. Zod reports the issues of the closest branch.

### Integers and exact decimals

Zorg reads a JSON number as an exact decimal, and Zod as a JavaScript double.

- `Z.Integer()` is a `long` and exact: it takes integers up to 2^63-1. `Z.Number<int>()` is an `int`. A value out of range is `invalid_type`. `z.int()` stops at 2^53-1, and `z.int32()` reports `too_big` or `too_small`.
- `1e400` is infinity in Zod and is refused. A typed Zorg number refuses it too, because it doesn't fit the type. It refuses a non-zero value that rounds to zero as well, such as `1e-400` for a `double`, where Zod reads `0` and accepts it. Otherwise the value you get out would differ from the one that was checked: `Z.Number().Positive()` would accept a number that reads as `0`.
- A schema imported from JSON Schema has no .NET type, so it stays exact: `1e400` passes `type: number` and `1e-400` is greater than `0`.
- `MultipleOf` is exact. `1e308` is a multiple of `0.1`, which Zod's floating-point remainder says it isn't.

### Formats

Zod and Zorg accept different sets for the same format. Zod uses its own regular expressions, or the WHATWG URL parser. Zorg follows the RFCs.

| Format | Zorg | Zod |
|---|---|---|
| `email` | RFC 5321, with a local part of at most 64 characters | its own pattern |
| `uri`, `url` | RFC 3986 | `z.url()` uses the WHATWG parser, so it accepts non-ASCII hosts and surrounding spaces |
| `uuid` | any UUID text | `z.uuid()` checks the version and the variant |
| `date-time` | RFC 3339, with an offset | `z.iso.datetime()` is UTC only by default |
| `time` | RFC 3339 `full-time`, with an offset | `z.iso.time()` has no offset |
| `hostname` | RFC 1123 | its own pattern |

A schema that Zorg emits and that Zod reads back is therefore stricter for `uuid` than the emitted document claims: Zod rejects UUIDs of an unknown version.

### Trim and case mapping

- `Trim` uses the .NET whitespace set, and Zod the JavaScript one. JavaScript trims U+FEFF and keeps U+0085, and .NET does the reverse.
- `ToUpperInvariant` and `ToLowerInvariant` map one character to one. JavaScript `toUpperCase` and `toLowerCase` use the full mappings, so the German sharp s becomes `SS`, the `fi` ligature becomes `FI`, and a capital I with a dot above becomes `i` plus a combining dot.

### Objects inside AllOf

Every branch of `Z.AllOf` sees the original value and rewrites it on its own, and the results are merged, as in Zod. A property stays if any branch keeps it, so with one branch for `a` and another for `b`, `{"a":1,"b":"y","x":2}` comes back as `{"a":1,"b":"y"}`. When two branches change one value to different results, the later branch wins, where Zod reports an error. Past 256 levels of nesting the later result stands as it is. In the output schema, objects under `allOf` aren't closed, because each branch only knows its own properties.

## Not in Zorg.Json

`Transform<TOut>`, `Pipe`, `Preprocess` and codecs don't exist. Convert with `ParseResult<T>.Map`, and leave representation changes to System.Text.Json converters. `.brand()` isn't needed: use a `readonly record struct`.
