using System;
using System.Text.Json;
using Xunit;
using Zorg.Json.Emission;

namespace Zorg.Json.Tests;

/// <summary>Emit(Import(Emit(s))) is byte-identical for schemas built with the fluent API.</summary>
public sealed class RoundTripTests
{
    private static readonly ObjectSchema Category = Z.Object()
        .Property("name", Z.String())
        .Property("children", Z.Array(Z.Lazy("category", () => Category!)).Optional());

    private static readonly JsonValueSchema Recursive = Z.AnyOf(Z.Integer(), Z.Array(Z.Lazy("recursive", () => Recursive!)));

    public static TheoryData<string, Func<JsonSchemaOptions, string>> Schemas { get; } = new()
    {
        { "string", Row(Z.String()) },
        { "string keywords", Row(Z.String().MinLength(1).MaxLength(5).Pattern("^a").Email()) },
        { "string annotations", Row(Z.String().Title("T").Description("D").Comment("C").Deprecated().ReadOnly().WriteOnly().Examples("a", "b")) },
        { "extensions", Row(Z.String().Meta("x-b", JsonElement.Parse("1")).Meta("x-a", JsonElement.Parse("""{ "n": [1] }"""))) },
        { "string const", Row(Z.String().Const("a")) },
        { "string enum", Row(Z.String().Enum("a", "b")) },
        { "nullable string", Row(Z.String().Nullable()) },
        { "nullable enum", Row(Z.String().Enum("a").Nullable()) },
        { "normalized", Row(Z.String().Trim().MaxLength(3).Pattern("a")) },
        { "refined", Row(Z.String().Refine(text => text.Length > 0, "Empty")) },
        { "integer", Row(Z.Integer()) },
        { "number keywords", Row(Z.Number().Minimum(0).Maximum(1.5).ExclusiveMinimum(-1).ExclusiveMaximum(2).MultipleOf(0.5)) },
        { "sized number", Row(Z.Number<int>().Minimum(0)) },
        { "decimal", Row(Z.Number<decimal>().MultipleOf(0.01m)) },
        { "coerced number", Row(Z.Integer().Coerce().Minimum(1)) },
        { "coerced boolean", Row(Z.Boolean().Coerce()) },
        { "boolean", Row(Z.Boolean().Const(true)) },
        { "null", Row(Z.Null()) },
        { "any", Row(Z.Any()) },
        { "never", Row(Z.Never()) },
        { "array", Row(Z.Array(Z.String()).MinItems(1).MaxItems(3).UniqueItems()) },
        { "contains", Row(Z.Array().Contains(Z.String(), 1, 2)) },
        { "tuple", Row(Z.Tuple(Z.String(), Z.Integer())) },
        { "strict object", Row(Z.Object().Property("a", Z.String()).Property("b", Z.Integer().Optional()).Property("c", Z.Integer().Default(1))) },
        { "strip object", Row(Z.Object().Strip().Property("a", Z.String())) },
        { "open object", Row(Z.Object().Passthrough().Property("a", Z.String())) },
        { "object keywords", Row(Z.Object().MinProperties(1).MaxProperties(2).PatternProperty("^a", Z.String()).PropertyNames(Z.String().MaxLength(3)).DependentRequired("a", "b")) },
        { "record", Row(Z.Record(Z.Integer())) },
        { "any of", Row(Z.AnyOf(Z.String(), Z.Integer())) },
        { "one of", Row(Z.OneOf(Z.String(), Z.Integer())) },
        { "all of", Row(Z.AllOf(Z.String().Pattern("^a"), Z.String().MaxLength(5))) },
        { "not", Row(Z.Not(Z.Null())) },
        { "if", Row(Z.If(Z.String(), Z.String().MinLength(1), Z.Integer())) },
        {
            "discriminated union",
            Row(Z.DiscriminatedUnion(
                "kind",
                Z.Object().Property("kind", Z.String().Const("circle")).Property("radius", Z.Number()),
                Z.Object().Property("kind", Z.String().Const("square")).Property("side", Z.Number())))
        },
        { "recursive object", Row(Category) },
        { "recursive union", Row(Recursive) },
        { "recursive with extension", Row(Category.Meta("x-a", JsonElement.Parse("1"))) },
    };

    private static Func<JsonSchemaOptions, string> Row<T>(Schema<T> schema) => options => schema.ToJsonSchema(options).ToJsonString();

    private static JsonSchemaOptions Options(string target, SchemaDirection direction) => new()
    {
        Target = target == "draft-07" ? JsonSchemaProfile.Draft07 : JsonSchemaProfile.Draft202012,
        Direction = direction,
    };

    [Theory]
    [MemberData(nameof(Schemas))]
    public void Emitting_an_import_of_an_emitted_schema_changes_nothing(string name, Func<JsonSchemaOptions, string> emit)
    {
        foreach (string target in (string[])["draft-2020-12", "draft-07"])
        {
            foreach (SchemaDirection direction in (SchemaDirection[])[SchemaDirection.Input, SchemaDirection.Output])
            {
                JsonSchemaOptions options = Options(target, direction);
                string emitted = emit(options);

                Assert.True(
                    emitted == Z.FromJsonSchema(emitted).ToJsonSchema(options).ToJsonString(),
                    $"{name}, {target}, {direction}:\n{emitted}\n{Z.FromJsonSchema(emitted).ToJsonSchema(options).ToJsonString()}");
            }
        }
    }

    [Fact]
    public void Named_schemas_written_once_round_trip()
    {
        JsonSchemaOptions options = new() { Reused = ReusedSchemaHandling.Ref };
        StringSchema shared = Z.String().Email().Named("email");
        string emitted = Z.Object().Property("a", shared).Property("b", shared).ToJsonSchema(options).ToJsonString();

        Assert.Equal(emitted, Z.FromJsonSchema(emitted).ToJsonSchema(options).ToJsonString());
    }

    [Fact]
    public void An_imported_schema_keeps_the_verdicts_of_the_original()
    {
        ObjectSchema original = Z.Object().Property("a", Z.String().MinLength(1)).Property("b", Z.Integer().Optional());
        JsonValueSchema imported = Z.FromJsonSchema(original.ToJsonSchema().ToJsonString());

        foreach (string document in (string[])["""{ "a": "x" }""", """{ "a": "" }""", """{ "a": "x", "b": 1.5 }""", """{ "a": "x", "c": 1 }""", "{}", "1"])
        {
            Assert.Equal(original.SafeParse(document).IsSuccess, imported.SafeParse(document).IsSuccess);
        }
    }
}
