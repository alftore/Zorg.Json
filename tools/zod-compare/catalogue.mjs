// The Zod side of every schema pair. A case is matched by `name` with the Zorg side in
// tests/Zorg.Json.ComplianceTests/ZodComparisonTests.cs. Keep both sides equivalent: the point is to find
// where they are not.
//
// `json: false` marks a schema with no JSON Schema form (refinements only, recursion through z.lazy is
// written with getters so it stays representable).
import { z } from "zod";

const person = { name: z.string(), age: z.int() };

const tree = z.strictObject({
  value: z.int(),
  get children() {
    return z.array(tree);
  },
});

const nested = z.union([
  z.number(),
  z.array(
    z.lazy(() => nested),
  ),
]);

const circle = z.strictObject({ kind: z.literal("circle"), radius: z.number() });
const square = z.strictObject({ kind: z.literal("square"), side: z.number() });
const triangle = z.strictObject({ kind: z.literal("triangle"), a: z.number(), b: z.number() });

/** @type {{ name: string, group: string, schema: import("zod").ZodType, json?: boolean }[]} */
export const cases = [
  // strings
  { name: "string", group: "string", schema: z.string() },
  { name: "string-min3", group: "string", schema: z.string().min(3) },
  { name: "string-max5", group: "string", schema: z.string().max(5) },
  { name: "string-len2-4", group: "string", schema: z.string().min(2).max(4) },
  { name: "string-length3", group: "string", schema: z.string().length(3) },
  { name: "string-regex-lower", group: "string", schema: z.string().regex(/^[a-z]+$/) },
  { name: "string-regex-digits", group: "string", schema: z.string().regex(/\d{3}/) },
  { name: "string-regex-dot2", group: "string", schema: z.string().regex(/^.{2}$/) },
  { name: "string-startswith", group: "string", schema: z.string().startsWith("ab") },
  { name: "string-email", group: "format", schema: z.email() },
  { name: "string-uuid", group: "format", schema: z.uuid() },
  { name: "string-guid", group: "format", schema: z.guid() },
  { name: "string-url", group: "format", schema: z.url() },
  { name: "string-datetime", group: "format", schema: z.iso.datetime() },
  { name: "string-datetime-offset", group: "format", schema: z.iso.datetime({ offset: true }) },
  { name: "string-date", group: "format", schema: z.iso.date() },
  { name: "string-time", group: "format", schema: z.iso.time() },
  { name: "string-ipv4", group: "format", schema: z.ipv4() },
  { name: "string-ipv6", group: "format", schema: z.ipv6() },
  { name: "string-hostname", group: "format", schema: z.hostname() },
  { name: "string-nullable", group: "string", schema: z.string().nullable() },
  { name: "string-trim-min1", group: "transform", schema: z.string().trim().min(1) },
  { name: "string-trim-max3", group: "transform", schema: z.string().trim().max(3) },
  { name: "string-max3-trim", group: "transform", schema: z.string().max(3).trim() },
  { name: "string-lowercase", group: "transform", schema: z.string().toLowerCase() },
  { name: "string-uppercase", group: "transform", schema: z.string().toUpperCase() },
  { name: "string-lowercase-pattern", group: "transform", schema: z.string().toLowerCase().regex(/^(web|news)$/) },
  { name: "string-normalize", group: "transform", schema: z.string().normalize() },
  { name: "string-overwrite", group: "transform", schema: z.string().overwrite((s) => s.replaceAll("-", "")) },

  // numbers and integers
  { name: "number", group: "number", schema: z.number() },
  { name: "number-min0-max10", group: "number", schema: z.number().min(0).max(10) },
  { name: "number-gt0-lt10", group: "number", schema: z.number().gt(0).lt(10) },
  { name: "number-negative", group: "number", schema: z.number().negative() },
  { name: "number-multipleof5", group: "number", schema: z.number().multipleOf(5) },
  { name: "number-multipleof-tenth", group: "number", schema: z.number().multipleOf(0.1) },
  { name: "number-nullable", group: "number", schema: z.number().nullable() },
  { name: "integer", group: "number", schema: z.int() },
  { name: "integer-0-100", group: "number", schema: z.int().min(0).max(100) },
  { name: "integer-int32", group: "number", schema: z.int32() },
  { name: "integer-multipleof3", group: "number", schema: z.int().multipleOf(3) },
  { name: "number-literal5", group: "literal", schema: z.literal(5) },
  { name: "number-enum", group: "literal", schema: z.literal([1, 2, 3]) },

  // booleans, null, literals, enums, any, never
  { name: "boolean", group: "literal", schema: z.boolean() },
  { name: "null", group: "literal", schema: z.null() },
  { name: "literal-string", group: "literal", schema: z.literal("a") },
  { name: "literal-true", group: "literal", schema: z.literal(true) },
  { name: "enum-colors", group: "literal", schema: z.enum(["red", "green", "blue"]) },
  { name: "any", group: "literal", schema: z.unknown() },
  { name: "never", group: "literal", schema: z.never() },

  // arrays and tuples
  { name: "array-string", group: "array", schema: z.array(z.string()) },
  { name: "array-number-1-3", group: "array", schema: z.array(z.number()).min(1).max(3) },
  { name: "array-nonempty", group: "array", schema: z.array(z.string()).nonempty() },
  {
    name: "array-unique",
    group: "array",
    schema: z.array(z.string()).refine((items) => new Set(items).size === items.length),
  },
  { name: "array-nested", group: "array", schema: z.array(z.array(z.int())) },
  { name: "array-nullable", group: "array", schema: z.array(z.int()).nullable() },
  { name: "array-of-objects", group: "array", schema: z.array(z.strictObject({ a: z.int() })) },
  { name: "tuple-string-int", group: "array", schema: z.tuple([z.string(), z.int()]) },
  { name: "tuple-three", group: "array", schema: z.tuple([z.string(), z.number(), z.boolean()]) },
  { name: "tuple-empty", group: "array", schema: z.tuple([]) },

  // objects
  { name: "object-strict", group: "object", schema: z.strictObject(person) },
  { name: "object-strip", group: "object", schema: z.object(person) },
  // The defaults, not an equivalent pair: z.object() strips unknown keys and Z.Object() rejects them.
  { name: "object-default-mode", group: "object", schema: z.object(person) },
  { name: "object-passthrough", group: "object", schema: z.looseObject(person) },
  { name: "object-optional", group: "object", schema: z.strictObject({ name: z.string(), age: z.int().optional() }) },
  { name: "object-nullable-prop", group: "object", schema: z.strictObject({ name: z.string().nullable(), age: z.int() }) },
  { name: "object-nullish-prop", group: "object", schema: z.strictObject({ name: z.string().nullish() }) },
  { name: "object-default-int", group: "object", schema: z.strictObject({ n: z.int().default(10) }) },
  { name: "object-default-string", group: "object", schema: z.strictObject({ s: z.string().default("x") }) },
  { name: "object-catchall", group: "object", schema: z.object({ id: z.int() }).catchall(z.string()) },
  { name: "object-nested", group: "object", schema: z.strictObject({ a: z.strictObject({ b: z.strictObject({ c: z.int() }) }) }) },
  { name: "object-empty-strict", group: "object", schema: z.strictObject({}) },
  { name: "object-nullable", group: "object", schema: z.strictObject(person).nullable() },
  { name: "object-pick", group: "object", schema: z.strictObject({ a: z.string(), b: z.int(), c: z.boolean() }).pick({ a: true, b: true }) },
  { name: "object-omit", group: "object", schema: z.strictObject({ a: z.string(), b: z.int(), c: z.boolean() }).omit({ c: true }) },
  { name: "object-partial", group: "object", schema: z.strictObject({ name: z.string(), age: z.int() }).partial() },
  { name: "object-extend", group: "object", schema: z.strictObject({ name: z.string() }).extend({ age: z.int() }) },
  { name: "object-pointer-keys", group: "object", schema: z.strictObject({ "a/b": z.string(), "c~d": z.string() }) },
  { name: "object-unicode-key", group: "object", schema: z.strictObject({ "\u{1F600}": z.int() }) },

  // records
  { name: "record-number", group: "record", schema: z.record(z.string(), z.number()) },
  { name: "record-object", group: "record", schema: z.record(z.string(), z.strictObject({ x: z.int() })) },
  { name: "record-lower-keys", group: "record", schema: z.record(z.string().regex(/^[a-z]+$/), z.number()) },

  // unions, discriminated unions, intersections
  { name: "union-string-number", group: "union", schema: z.union([z.string(), z.number()]) },
  { name: "union-string-int-null", group: "union", schema: z.union([z.string(), z.int(), z.null()]) },
  { name: "union-array-or-single", group: "union", schema: z.union([z.string(), z.array(z.string())]) },
  { name: "union-objects", group: "union", schema: z.union([z.strictObject({ a: z.int() }), z.strictObject({ b: z.string() })]) },
  { name: "xor-number-int", group: "union", schema: z.xor([z.number(), z.int()]) },
  { name: "discriminated-two", group: "union", schema: z.discriminatedUnion("kind", [circle, square]) },
  { name: "discriminated-three", group: "union", schema: z.discriminatedUnion("kind", [circle, square, triangle]) },
  { name: "intersection-string-lengths", group: "intersection", schema: z.intersection(z.string().min(2), z.string().max(4)) },
  { name: "intersection-number-range", group: "intersection", schema: z.intersection(z.number().min(0), z.number().max(10)) },
  {
    name: "intersection-open-objects",
    group: "intersection",
    schema: z.intersection(z.looseObject({ a: z.int() }), z.looseObject({ b: z.string() })),
  },
  {
    name: "intersection-strip-objects",
    group: "intersection",
    schema: z.intersection(z.object({ a: z.int() }), z.object({ b: z.string() })),
  },
  {
    name: "intersection-strict-objects",
    group: "intersection",
    schema: z.intersection(z.strictObject({ a: z.int() }), z.strictObject({ b: z.string() })),
  },

  // coercion
  { name: "coerce-number", group: "coerce", schema: z.coerce.number() },
  { name: "coerce-integer", group: "coerce", schema: z.coerce.number().int() },
  { name: "coerce-boolean", group: "coerce", schema: z.coerce.boolean() },
  { name: "coerce-number-prop", group: "coerce", schema: z.strictObject({ n: z.coerce.number() }) },

  // refinements
  { name: "refine-not-admin", group: "refine", schema: z.string().refine((s) => s !== "admin") },
  { name: "refine-even", group: "refine", schema: z.int().refine((n) => n % 2 === 0) },
  {
    name: "refine-min-max",
    group: "refine",
    schema: z.strictObject({ min: z.int(), max: z.int() }).refine((o) => o.min <= o.max),
  },

  // recursion
  { name: "recursive-tree", group: "recursion", schema: tree },
  { name: "recursive-nested", group: "recursion", schema: nested },
];
