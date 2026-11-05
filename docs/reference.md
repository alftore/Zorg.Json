# Keyword and Zod reference

Method names follow JSON Schema keywords first. Coming from Zod, look up the name here.

## Types

| JSON Schema | Zorg.Json | Zod |
|---|---|---|
| `type: string` | `Z.String()` | `z.string()` |
| `type: number` | `Z.Number()`, `Z.Number<decimal>()` | `z.number()` |
| `type: integer` | `Z.Integer()`, `Z.Number<int>()` | `z.int()`, `z.int32()` |
| `type: boolean` | `Z.Boolean()` | `z.boolean()` |
| `type: null` | `Z.Null()` | `z.null()` |
| `{}` | `Z.Any()` | `z.unknown()` |
| `false` | `Z.Never()` | `z.never()` |
| `type: array`, `items` | `Z.Array(items)` | `z.array()` |
| `prefixItems` | `Z.Tuple(a, b)` | `z.tuple()` |
| `type: object` | `Z.Object()`, `Schema.Object(typeInfo)` | `z.strictObject()` |
| `additionalProperties: schema` | `Z.Record(values)` | `z.record()` |
| `format` with a typed result | `Schema.Parsable<Guid>("uuid")` | `z.uuid()` |

## Strings

| JSON Schema | Zorg.Json | Zod |
|---|---|---|
| `minLength` | `.MinLength(n)` | `.min(n)` |
| `maxLength` | `.MaxLength(n)` | `.max(n)` |
| `pattern` | `.Pattern(regex)` | `.regex()` |
| `format` | `.Format(name)` | |
| `format: email` | `.Email()` | `z.email()` |
| `format: uuid` | `.Uuid()` | `z.uuid()` |
| `format: uri` | `.Uri()` | `z.url()` |
| `format: date-time` | `.DateTime()` | `z.iso.datetime()` |
| `format: date` | `.Date()` | `z.iso.date()` |
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
| `additionalProperties: false` | `.Strict()` (the default) | `.strict()` |
| no `additionalProperties` | `.Passthrough()` | `.passthrough()` |
| | `.Strip()` | `.strip()` |
| `additionalProperties: schema` | `.AdditionalProperties(schema)` | `.catchall()` |
| `patternProperties` | `.PatternProperty(pattern, schema)` | |
| `propertyNames` | `.PropertyNames(schema)` | |
| `minProperties`, `maxProperties` | `.MinProperties(n)`, `.MaxProperties(n)` | |
| `dependentRequired` | `.DependentRequired(name, others)` | |
| | `.Pick(names)`, `.Omit(names)`, `.Partial()`, `.Extend(other)` | same names |

`ObjectSchema<T>` adds `.Property(p => p.Name, s => ...)`, `.Property(p => p.Home, schema)`, `.Optional(p => p.X)`, `.Omit(p => p.X)` and `.Refine(predicate, message, p => p.X)`.

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
| `x-*` | `.Meta(keyword, value)` | `.meta()` |
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
| `schema.Parse(json)` | `schema.parse(value)` |
| `result.Value`, `result.Issues` | `result.data`, `result.error.issues` |
| `issues.ToDictionary()` | `z.flattenError()` |
| `issues.ToPrettyString()` | `z.prettifyError()` |
| `schema.ToJsonSchema()` | `z.toJSONSchema(schema)` |
| `Direction = Input` (default) | `io: "input"` (Zod's default is output) |
| `Target = JsonSchemaProfile.Draft07` | `target: "draft-07"` |
| `Unrepresentable = Throw` | `unrepresentable: "throw"` |
| `schema.GenerateSample()` (`Zorg.Json.Sampling`) | none. NJsonSchema: `SampleJsonDataGenerator` |

## Not in Zorg.Json

`Transform<TOut>`, `Pipe`, `Preprocess` and codecs don't exist. Convert with `ParseResult<T>.Map`, and leave representation changes to System.Text.Json converters. `.brand()` isn't needed: use a `readonly record struct`.
