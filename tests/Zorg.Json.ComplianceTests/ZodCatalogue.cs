using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using Zorg.Json.Emission;

namespace Zorg.Json.ComplianceTests;

/// <summary>What one parse did, in the same vocabulary as the fixture: <c>code@/json/pointer</c> per issue.</summary>
internal sealed record ZodOutcome(bool Accepted, string? Output, ImmutableArray<string> Issues, string? Crash);

/// <summary>A catalogue schema, as the Zorg side runs it.</summary>
internal sealed record ZorgCase(string Name, Func<string, ZodOutcome> Parse, Func<JsonSchemaOptions, EmitResult> Emit);

/// <summary>The Zorg side of tools/zod-compare/catalogue.mjs, matched by name.</summary>
internal static class ZodCatalogue
{
    private static readonly ObjectSchema Tree = Z.Object()
        .Property("value", Z.Integer())
        .Property("children", Z.Array(Z.Lazy("tree", () => Tree!)));

    private static readonly JsonValueSchema Nested = Z.AnyOf(Z.Number(), Z.Array(Z.Lazy("nested", () => Nested!)));

    private static readonly ObjectSchema Circle = Z.Object()
        .Property("kind", Z.String().Const("circle"))
        .Property("radius", Z.Number());

    private static readonly ObjectSchema Square = Z.Object()
        .Property("kind", Z.String().Const("square"))
        .Property("side", Z.Number());

    private static readonly ObjectSchema Triangle = Z.Object()
        .Property("kind", Z.String().Const("triangle"))
        .Property("a", Z.Number())
        .Property("b", Z.Number());

    private static readonly ObjectSchema Person = Z.Object()
        .Property("name", Z.String())
        .Property("age", Z.Integer());

    public static IReadOnlyDictionary<string, ZorgCase> Cases { get; } = Build();

    private static Dictionary<string, ZorgCase> Build()
    {
        Dictionary<string, ZorgCase> cases = [];

        void Add<T>(string name, Schema<T> schema) =>
            cases.Add(name, new ZorgCase(name, json => Run(schema, json), options => schema.Emit(options)));

        // strings
        Add("string", Z.String());
        Add("string-min3", Z.String().MinLength(3));
        Add("string-max5", Z.String().MaxLength(5));
        Add("string-len2-4", Z.String().MinLength(2).MaxLength(4));
        Add("string-length3", Z.String().MinLength(3).MaxLength(3));
        Add("string-regex-lower", Z.String().Pattern("^[a-z]+$"));
        Add("string-regex-digits", Z.String().Pattern("\\d{3}"));
        Add("string-regex-dot2", Z.String().Pattern("^.{2}$"));
        Add("string-startswith", Z.String().Pattern("^ab"));
        Add("string-email", Z.String().Email());
        Add("string-uuid", Z.String().Uuid());
        Add("string-guid", Z.String().Uuid());
        Add("string-url", Z.String().Uri());
        Add("string-datetime", Z.String().DateTime());
        Add("string-datetime-offset", Z.String().DateTime());
        Add("string-date", Z.String().Date());
        Add("string-time", Z.String().Format("time"));
        Add("string-ipv4", Z.String().Format("ipv4"));
        Add("string-ipv6", Z.String().Format("ipv6"));
        Add("string-hostname", Z.String().Format("hostname"));
        Add("string-nullable", Z.String().Nullable());
        Add("string-trim-min1", Z.String().Trim().MinLength(1));
        Add("string-trim-max3", Z.String().Trim().MaxLength(3));
        Add("string-max3-trim", Z.String().MaxLength(3).Trim());
        Add("string-lowercase", Z.String().ToLowerInvariant());
        Add("string-uppercase", Z.String().ToUpperInvariant());
        Add("string-lowercase-pattern", Z.String().ToLowerInvariant().Pattern("^(web|news)$"));
        Add("string-normalize", Z.String().NormalizeUnicode());
        Add("string-overwrite", Z.String().Overwrite(text => text.Replace("-", string.Empty, StringComparison.Ordinal)));

        // numbers and integers
        Add("number", Z.Number());
        Add("number-min0-max10", Z.Number().Minimum(0).Maximum(10));
        Add("number-gt0-lt10", Z.Number().ExclusiveMinimum(0).ExclusiveMaximum(10));
        Add("number-negative", Z.Number().Negative());
        Add("number-multipleof5", Z.Number().MultipleOf(5));
        Add("number-multipleof-tenth", Z.Number().MultipleOf(0.1));
        Add("number-nullable", Z.Number().Nullable());
        Add("integer", Z.Integer());
        Add("integer-0-100", Z.Integer().Minimum(0).Maximum(100));
        Add("integer-int32", Z.Number<int>());
        Add("integer-multipleof3", Z.Integer().MultipleOf(3));
        Add("number-literal5", Z.Number().Const(5));
        Add("number-enum", Z.Number().Enum(1, 2, 3));

        // booleans, null, literals, enums, any, never
        Add("boolean", Z.Boolean());
        Add("null", Z.Null());
        Add("literal-string", Z.String().Const("a"));
        Add("literal-true", Z.Boolean().Const(true));
        Add("enum-colors", Z.String().Enum("red", "green", "blue"));
        Add("any", Z.Any());
        Add("never", Z.Never());

        // arrays and tuples
        Add("array-string", Z.Array(Z.String()));
        Add("array-number-1-3", Z.Array(Z.Number()).MinItems(1).MaxItems(3));
        Add("array-nonempty", Z.Array(Z.String()).NonEmpty());
        Add("array-unique", Z.Array(Z.String()).UniqueItems());
        Add("array-nested", Z.Array(Z.Array(Z.Integer())));
        Add("array-nullable", Z.Array(Z.Integer()).Nullable());
        Add("array-of-objects", Z.Array(Z.Object().Property("a", Z.Integer())));
        Add("tuple-string-int", Z.Tuple(Z.String(), Z.Integer()));
        Add("tuple-three", Z.Tuple(Z.String(), Z.Number(), Z.Boolean()));
        Add("tuple-empty", Z.Tuple());

        // objects
        Add("object-strict", Person);
        Add("object-strip", Person.Strip());
        Add("object-default-mode", Person);
        Add("object-passthrough", Person.Passthrough());
        Add("object-optional", Z.Object().Property("name", Z.String()).Property("age", Z.Integer().Optional()));
        Add("object-nullable-prop", Z.Object().Property("name", Z.String().Nullable()).Property("age", Z.Integer()));
        Add("object-nullish-prop", Z.Object().Property("name", Z.String().Nullable().Optional()));
        Add("object-default-int", Z.Object().Property("n", Z.Integer().Default(10)));
        Add("object-default-string", Z.Object().Property("s", Z.String().Default("x")));
        Add("object-catchall", Z.Object().Property("id", Z.Integer()).AdditionalProperties(Z.String()));
        Add("object-nested", Z.Object().Property("a", Z.Object().Property("b", Z.Object().Property("c", Z.Integer()))));
        Add("object-empty-strict", Z.Object());
        Add("object-nullable", Person.Nullable());
        Add("object-pick", Abc.Pick("a", "b"));
        Add("object-omit", Abc.Omit("c"));
        Add("object-partial", Person.Partial());
        Add("object-extend", Z.Object().Property("name", Z.String()).Extend(Z.Object().Property("age", Z.Integer())));
        Add("object-pointer-keys", Z.Object().Property("a/b", Z.String()).Property("c~d", Z.String()));
        Add("object-unicode-key", Z.Object().Property("\U0001F600", Z.Integer()));

        // records
        Add("record-number", Z.Record(Z.Number()));
        Add("record-object", Z.Record(Z.Object().Property("x", Z.Integer())));
        Add("record-lower-keys", Z.Record(Z.Number()).PropertyNames(Z.String().Pattern("^[a-z]+$")));

        // unions, discriminated unions, intersections
        Add("union-string-number", Z.AnyOf(Z.String(), Z.Number()));
        Add("union-string-int-null", Z.AnyOf(Z.String(), Z.Integer(), Z.Null()));
        Add("union-array-or-single", Z.AnyOf(Z.String(), Z.Array(Z.String())));
        Add("union-objects", Z.AnyOf(Z.Object().Property("a", Z.Integer()), Z.Object().Property("b", Z.String())));
        Add("xor-number-int", Z.OneOf(Z.Number(), Z.Integer()));
        Add("discriminated-two", Z.DiscriminatedUnion("kind", Circle, Square));
        Add("discriminated-three", Z.DiscriminatedUnion("kind", Circle, Square, Triangle));
        Add("intersection-string-lengths", Z.AllOf(Z.String().MinLength(2), Z.String().MaxLength(4)));
        Add("intersection-number-range", Z.AllOf(Z.Number().Minimum(0), Z.Number().Maximum(10)));
        Add(
            "intersection-open-objects",
            Z.AllOf(Z.Object().Property("a", Z.Integer()).Passthrough(), Z.Object().Property("b", Z.String()).Passthrough()));
        Add(
            "intersection-strip-objects",
            Z.AllOf(Z.Object().Property("a", Z.Integer()).Strip(), Z.Object().Property("b", Z.String()).Strip()));
        Add(
            "intersection-strict-objects",
            Z.AllOf(Z.Object().Property("a", Z.Integer()), Z.Object().Property("b", Z.String())));

        // coercion
        Add("coerce-number", Z.Number().Coerce());
        Add("coerce-integer", Z.Integer().Coerce());
        Add("coerce-boolean", Z.Boolean().Coerce());
        Add("coerce-number-prop", Z.Object().Property("n", Z.Number().Coerce()));

        // refinements
        Add("refine-not-admin", Z.String().Refine(text => text != "admin", "reserved"));
        Add("refine-even", Z.Integer().Refine(number => number % 2 == 0, "must be even"));
        Add(
            "refine-min-max",
            Z.Object().Property("min", Z.Integer()).Property("max", Z.Integer())
                .Refine(value => value.GetProperty("min").GetInt64() <= value.GetProperty("max").GetInt64(), "min above max"));

        // recursion
        Add("recursive-tree", Tree);
        Add("recursive-nested", Nested);

        return cases;
    }

    private static ObjectSchema Abc => Z.Object()
        .Property("a", Z.String())
        .Property("b", Z.Integer())
        .Property("c", Z.Boolean());

    private static ZodOutcome Run<T>(Schema<T> schema, string json)
    {
        try
        {
            ParseResult<T> result = schema.SafeParse(json);

            if (!result.IsSuccess)
            {
                return new ZodOutcome(false, null, [.. result.Issues.Select(issue => $"{issue.Code}@{issue.InstanceLocation}")], null);
            }

            return new ZodOutcome(true, Serialize(result.Value), [], null);
        }
#pragma warning disable CA1031 // SafeParse must not throw for any input, so any exception is a finding to record.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return new ZodOutcome(false, null, [], $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static string Serialize<T>(T value) => value is JsonElement element ? element.GetRawText() : JsonSerializer.Serialize(value);
}
