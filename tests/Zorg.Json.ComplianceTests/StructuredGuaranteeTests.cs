using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Json.Schema;
using Xunit;
using Zorg.Json.Emission;

namespace Zorg.Json.ComplianceTests;

public sealed record Item(string Name, int Count, string? Note = null);

[JsonSerializable(typeof(Item))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
public sealed partial class ComplianceJson : JsonSerializerContext;

/// <summary>Extends <see cref="InputGuaranteeTests"/> to every kind of schema, with generated documents.</summary>
public sealed class StructuredGuaranteeTests
{
    private const int DocumentCount = 400;

    private static readonly string[] Scalars =
    [
        "null", "true", "false", "0", "1", "-1", "1.0", "1.5", "2", "10", "1e2", "5", "50", "51", "2147483648", "0.1", "0.3",
        "\"\"", "\"a\"", "\" a \"", "\"ab\"", "\"true\"", "\"1\"", "\"42\"", "\"x\"", "\"circle\"", "\"square\"",
    ];

    private static readonly string[] Objects =
    [
        """{"name":" a "}""", """{"name":"a"}""", """{"kind":"square"}""", """{"kind":"circle","radius":1}""",
        """{"name":"a","count":1}""", """{"name":"a","count":1,"note":null}""", """{"a":"x","b":1}""", """{"a":1,"b":[{"a":2}]}""",
    ];

    private static readonly string[] Keys = ["a", "b", "name", "count", "note", "kind", "radius", "x-y", "other"];

    public static TheoryData<string, ISchema> Exact { get; } = new()
    {
        { "number limits", Z.Number().Minimum(1).Maximum(50) },
        { "exclusive", Z.Number().ExclusiveMinimum(0).ExclusiveMaximum(2) },
        { "multipleOf", Z.Number<decimal>().MultipleOf(0.1m) },
        { "boolean", Z.Boolean() },
        { "null", Z.Null() },
        { "any", Z.Any() },
        { "never", Z.Never() },
        { "nullable string", Z.String().Nullable() },
        { "array", Z.Array(Z.String()).MinItems(1).MaxItems(2) },
        { "unique", Z.Array().UniqueItems() },
        { "contains", Z.Array().Contains(Z.String(), minimum: 1, maximum: 2) },
        { "tuple", Z.Tuple(Z.String(), Z.Number()) },
        { "closed object", Z.Object().Property("a", Z.String()).Property("b", Z.Number().Optional()) },
        { "open object", Z.Object().Passthrough().Property("a", Z.String()) },
        { "additional", Z.Object().AdditionalProperties(Z.Number()) },
        { "pattern properties", Z.Object().PatternProperty("^x-", Z.String()) },
        { "property names", Z.Object().Passthrough().PropertyNames(Z.String().MaxLength(1)) },
        { "property counts", Z.Object().Passthrough().MinProperties(1).MaxProperties(2) },
        { "dependent", Z.Object().Passthrough().DependentRequired("a", "b") },
        { "anyOf", Z.AnyOf(Z.String(), Z.Number()) },
        { "oneOf", Z.OneOf(Z.Number(), Z.Number().Minimum(1)) },
        { "allOf", Z.AllOf(Z.String().MinLength(1), Z.String().MaxLength(2)) },
        { "not", Z.Not(Z.String()) },
        { "if", Z.If(Z.String(), Z.String().MinLength(1), Z.Number()) },
        { "lazy", Tree.Schema },
    };

    public static TheoryData<string, ISchema> Looser { get; } = new()
    {
        { "integer", Z.Integer().Minimum(1) },
        { "int32", Z.Number<int>() },
        { "byte", Z.Number<byte>() },
        { "coerced integer", Z.Integer().Coerce().Minimum(1) },
        { "coerced number", Z.Number().Coerce().Const(5) },
        { "coerced boolean", Z.Boolean().Coerce() },
        { "default", Z.Object().Property("a", Z.Integer().Default(1)) },
        { "strip", Z.Object().Strip().Property("a", Z.String()) },
        { "trim in object", Z.Object().Property("name", Z.String().Trim().MaxLength(1)) },
        { "trim in array", Z.Array(Z.String().Trim().MaxLength(1)) },
        { "refine in object", Z.Object().Passthrough().Property("a", Z.Number().Refine(number => number > 1, "Too small")) },
        {
            "discriminated",
            Z.DiscriminatedUnion(
                "kind",
                Z.Object().Property("kind", Z.String().Const("circle")).Property("radius", Z.Number()),
                Z.Object().Property("kind", Z.String().Const("square")))
        },
        { "typed", Schema.Object(ComplianceJson.Default.Item).Property(item => item.Count, s => s.Minimum(0)) },
    };

    private static Func<string, bool> Parser(ISchema schema) => schema switch
    {
        Schema<JsonElement> element => document => element.SafeParse(document).IsSuccess,
        Schema<string> text => document => text.SafeParse(document).IsSuccess,
        Schema<double> number => document => number.SafeParse(document).IsSuccess,
        Schema<decimal> number => document => number.SafeParse(document).IsSuccess,
        Schema<long> number => document => number.SafeParse(document).IsSuccess,
        Schema<int> number => document => number.SafeParse(document).IsSuccess,
        Schema<byte> number => document => number.SafeParse(document).IsSuccess,
        Schema<bool> boolean => document => boolean.SafeParse(document).IsSuccess,
        Schema<Item> item => document => item.SafeParse(document).IsSuccess,
        Schema<IReadOnlyList<string>> list => document => list.SafeParse(document).IsSuccess,
        Schema<IReadOnlyList<JsonElement>> list => document => list.SafeParse(document).IsSuccess,
        _ => throw new NotSupportedException(schema.GetType().Name),
    };

    private static JsonSchema Emitted(ISchema schema) => schema switch
    {
        Schema<JsonElement> element => FromText(element.Emit().Schema),
        Schema<string> text => FromText(text.Emit().Schema),
        Schema<double> number => FromText(number.Emit().Schema),
        Schema<decimal> number => FromText(number.Emit().Schema),
        Schema<long> number => FromText(number.Emit().Schema),
        Schema<int> number => FromText(number.Emit().Schema),
        Schema<byte> number => FromText(number.Emit().Schema),
        Schema<bool> boolean => FromText(boolean.Emit().Schema),
        Schema<Item> item => FromText(item.Emit().Schema),
        Schema<IReadOnlyList<string>> list => FromText(list.Emit().Schema),
        Schema<IReadOnlyList<JsonElement>> list => FromText(list.Emit().Schema),
        _ => throw new NotSupportedException(schema.GetType().Name),
    };

    private static JsonSchema FromText(System.Text.Json.Nodes.JsonObject schema) => JsonSchema.FromText(schema.ToJsonString());

    /// <summary>The seed is fixed, so a failure reproduces.</summary>
    private static IEnumerable<string> Documents()
    {
        Random random = new(20261103);

        foreach (string document in Scalars.Concat(Objects))
        {
            yield return document;
        }

        for (int index = 0; index < DocumentCount; index++)
        {
            yield return Document(random, depth: 0);
        }
    }

    private static string Document(Random random, int depth)
    {
        int kind = random.Next(depth < 3 ? 4 : 2);

        return kind switch
        {
            2 => $"[{string.Join(',', Enumerable.Range(0, random.Next(4)).Select(_ => Document(random, depth + 1)))}]",
            3 => $"{{{string.Join(',', Keys.OrderBy(_ => random.Next()).Take(random.Next(5)).Select(key => $"\"{key}\":{Document(random, depth + 1)}"))}}}",
            _ => Scalars[random.Next(Scalars.Length)],
        };
    }

    [Theory]
    [MemberData(nameof(Exact))]
    public void Emitted_schema_agrees_with_parsing(string name, ISchema schema)
    {
        Func<string, bool> parse = Parser(schema);
        JsonSchema emitted = Emitted(schema);

        foreach (string document in Documents())
        {
            Assert.True(parse(document) == emitted.Evaluate(JsonElement.Parse(document)).IsValid, $"{name} disagrees on {document}");
        }
    }

    [Theory]
    [MemberData(nameof(Exact))]
    [MemberData(nameof(Looser))]
    public void Emitted_schema_is_never_stricter_than_parsing(string name, ISchema schema)
    {
        Func<string, bool> parse = Parser(schema);
        JsonSchema emitted = Emitted(schema);
        int accepted = 0;

        foreach (string document in Documents())
        {
            if (parse(document))
            {
                accepted++;
                Assert.True(emitted.Evaluate(JsonElement.Parse(document)).IsValid, $"{name} is stricter than parsing on {document}");
            }
        }

        Assert.True(accepted > 0 || name == "never", $"{name} accepted no document, so it proves nothing");
    }

    [Fact]
    public void Output_schema_describes_the_parsed_value()
    {
        ObjectSchema schema = Z.Object()
            .Property("name", Z.String().Trim().MaxLength(3))
            .Property("limit", Z.Integer().Coerce().Default(10))
            .Strip();

        JsonElement parsed = schema.Parse("""{ "name": "  abc  ", "unknown": 1 }""");
        JsonSchema output = FromText(schema.ToJsonSchema(new() { Direction = SchemaDirection.Output }));

        Assert.True(output.Evaluate(parsed).IsValid, parsed.GetRawText());
    }

    private static class Tree
    {
        public static readonly ObjectSchema Schema = Z.Object()
            .Property("a", Z.Number().Optional())
            .Property("b", Z.Array(Z.Lazy("tree", () => Schema!)).Optional());
    }
}
