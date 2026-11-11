#if NET11_0_OR_GREATER
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Json.Schema;
using Xunit;
using Zorg.Json.Emission;

namespace Zorg.Json.ComplianceTests;

public sealed record GuaranteeCat(string Name, int Lives);

public sealed record GuaranteeDog(string Name, bool Good, GuaranteeCat? Friend);

// A C# union compiles to a struct. These are test data, compared through their JSON, so CA1815 doesn't apply.
#pragma warning disable CA1815
public union GuaranteePet(GuaranteeCat, GuaranteeDog);

public union GuaranteeScalar(string?, long, bool);
#pragma warning restore CA1815

public sealed record GuaranteeHome(GuaranteePet Pet, IReadOnlyList<GuaranteePet>? Others, GuaranteeScalar Tag);

public sealed record GuaranteeTagged(GuaranteeScalar Tag, IReadOnlyList<GuaranteeScalar>? Tags);

public sealed record GuaranteeNumbers(BFloat16 Brain, Decimal32 Small, Decimal64 Medium, Decimal128 Large);

/// <summary>The input and output guarantees for what the .NET 11 build reads: C# unions and the new number types.</summary>
public sealed partial class StructuredGuaranteeTests
{
    private static readonly string[] Pets =
    [
        """{"name":"Tom","lives":3}""", """{"name":"Rex","good":true}""", """{"name":"Rex","good":true,"friend":{"name":"Tom","lives":1}}""",
        """{"name":"Rex","good":true,"friend":{"name":"Tom","lives":1,"junk":2}}""", """{"name":"Rex","good":true,"junk":1}""",
        """{"name":"Rex"}""", """{"lives":1}""", "{}", "1", "\"x\"", "null", "[]",
    ];

    private static readonly string[] Tags = ["\"a\"", "null", "1", "-5", "1.5", "true", "[]", "{}", "\"1\""];

    private static IEnumerable<string> Homes()
    {
        foreach (string pet in Pets)
        {
            foreach (string tag in Tags)
            {
                yield return $$"""{"pet":{{pet}},"tag":{{tag}}}""";
            }

            yield return $$"""{"pet":{{pet}},"others":[{{pet}},{"name":"Tom","lives":2}],"tag":"t"}""";
            yield return $$"""{"pet":{{pet}},"others":null,"tag":"t","extra":[1]}""";
        }

        yield return """{"tag":"t"}""";
    }

    private static JsonSerializerOptions UnionOptions(bool structural)
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

        if (structural)
        {
            options.TypeClassifiers.Add(new JsonUnionTypeStructuralClassifier());
        }

        // Without it, a string reads as a long too, and the default classifier refuses every string as ambiguous.
        options.NumberHandling = JsonNumberHandling.Strict;
        options.MakeReadOnly();
        return options;
    }

    /// <summary>Two object cases need the structural classifier: the default one reads by token kind and refuses them all.</summary>
    [Fact]
    public void A_union_of_objects_is_never_stricter_than_parsing_and_its_output_describes_the_result() =>
        CheckUnion(Schema.Object<GuaranteeHome>(UnionOptions(structural: true)), UnionOptions(structural: true), Homes());

    [Fact]
    public void A_union_of_scalars_is_never_stricter_than_parsing_and_its_output_describes_the_result()
    {
        JsonSerializerOptions options = UnionOptions(structural: false);
        IEnumerable<string> documents = Tags.SelectMany(tag => new[]
        {
            $$"""{"tag":{{tag}}}""",
            $$"""{"tag":{{tag}},"tags":[{{tag}},"a",2,false,null]}""",
            "{\"tag\":" + tag + ",\"tags\":null,\"extra\":{\"tag\":1}}",
        });

        CheckUnion(Schema.Object<GuaranteeTagged>(options), options, documents);
    }

    private static void CheckUnion<T>(ObjectSchema<T> schema, JsonSerializerOptions options, IEnumerable<string> documents)
    {
        int accepted = 0;

        foreach (JsonSchemaProfile profile in new[] { JsonSchemaProfile.Draft202012, JsonSchemaProfile.Draft07, JsonSchemaProfile.OpenApi31 })
        {
            JsonSchema input = FromText(schema.Emit(new() { Target = profile }).Schema);
            JsonSchema output = FromText(schema.Emit(new() { Target = profile, Direction = SchemaDirection.Output }).Schema);

            foreach (string document in documents)
            {
                ParseResult<T> parsed = schema.SafeParse(document);

                if (!parsed.IsSuccess)
                {
                    continue;
                }

                accepted++;
                Assert.True(input.Evaluate(JsonElement.Parse(document)).IsValid, $"{profile} input is stricter than parsing on {document}");

                JsonElement written = JsonSerializer.SerializeToElement(parsed.Value, options);
                Assert.True(output.Evaluate(written).IsValid, $"{profile} output refuses {written.GetRawText()}");
                Assert.True(schema.Validate(parsed.Value).IsSuccess, $"Validate refuses its own result for {document}");
            }
        }

        Assert.True(accepted > 0, "No document was accepted, so the test proves nothing");
    }

    [Theory]
    [InlineData("0"), InlineData("1.5"), InlineData("-2"), InlineData("1e30"), InlineData("3.3e38"), InlineData("3.4e38"), InlineData("1e300"), InlineData("1e400")]
    [InlineData("1e7000"), InlineData("0.1"), InlineData("1e-50"), InlineData("1e-7000"), InlineData("\"1\""), InlineData("null")]
    public void The_new_number_types_are_never_stricter_than_parsing(string value)
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        ObjectSchema<GuaranteeNumbers> schema = Schema.Object<GuaranteeNumbers>(options);
        JsonSchema input = FromText(schema.ToJsonSchema());

        foreach (string name in new[] { "brain", "small", "medium", "large" })
        {
            string document = $$"""{"brain":1,"small":1,"medium":1,"large":1,"{{name}}":{{value}}}""".Replace($"\"{name}\":1,", "", System.StringComparison.Ordinal);

            if (schema.SafeParse(document).IsSuccess)
            {
                Assert.True(input.Evaluate(JsonElement.Parse(document)).IsValid, $"{name} input is stricter than parsing on {value}");
            }
        }

        Check(Z.Number<BFloat16>(), value);
        Check(Z.Number<Decimal32>(), value);
        Check(Z.Number<Decimal64>(), value);
        Check(Z.Number<Decimal128>(), value);
    }

    private static void Check<TNumber>(NumberSchema<TNumber> schema, string value)
        where TNumber : struct, INumber<TNumber>
    {
        if (schema.SafeParse(value).IsSuccess)
        {
            Assert.True(FromText(schema.ToJsonSchema()).Evaluate(JsonElement.Parse(value)).IsValid, $"{typeof(TNumber).Name} input is stricter than parsing on {value}");
        }
    }
}
#endif
