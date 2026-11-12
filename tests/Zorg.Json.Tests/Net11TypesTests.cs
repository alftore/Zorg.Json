#if NET11_0_OR_GREATER
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Xunit;

namespace Zorg.Json.Tests;

// A C# union compiles to a struct. These are test data, compared through their JSON, so CA1815 doesn't apply.
#pragma warning disable CA1815

public sealed record Kitten(string Name, int Lives);

public sealed record Puppy(string Name, bool Good);

public union Pet(Kitten, Puppy);

public sealed record PetOwner(Pet Pet);

public union Scalar(string, int);

public sealed record ScalarHolder(Scalar Value);

public union MaybeText(string?, int);

public sealed record MaybeTextHolder(MaybeText Value);

public union Loose(JsonElement, int);

public sealed record LooseHolder(Loose Value);

public sealed class Measurements
{
    public BFloat16 Brain { get; set; }

    public Decimal32 Small { get; set; }

    public Decimal64 Medium { get; set; }

    public Decimal128 Large { get; set; }

    public BFloat16? Maybe { get; set; }
}

public sealed record Box(int X);

public sealed record WideBox(int X, int Y);

public sealed record Kit(string Name, Box Data)
{
    public int Lives { get; set; }
}

public sealed record Pup(string Name, bool Good, WideBox Data);

public union Litter(Kit, Pup);

public sealed record Litters(Litter One, IReadOnlyList<Litter> Many);

public sealed record Disc(double R);

public sealed record Group(string Name, Figure Child);

[JsonUnion(TypeClassifier = typeof(KindClassifier))]
public union Figure(Disc, Group);

public sealed record Drawing(Figure Figure);

public union Wider(Scalar, bool);

public sealed record WideHolder(Wider Wide);

public sealed record Shapes(Pet? Maybe, [property: JsonConverter(typeof(OptionalJsonConverter<Pet>))] Optional<Pet> Later);

public sealed class Unset
{
    public Scalar Value { get; set; }
}

/// <summary>Picks the case from a "kind" property that neither case declares, so removing it would break the choice.</summary>
public sealed class KindClassifier : JsonTypeClassifierFactory
{
    public override bool CanClassify(JsonTypeClassifierContext context) => true;

    public override JsonTypeClassifier CreateJsonClassifier(JsonTypeClassifierContext context, JsonSerializerOptions options) =>
        (ref Utf8JsonReader reader) =>
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            Utf8JsonReader copy = reader;
            copy.Read();

            while (copy.TokenType == JsonTokenType.PropertyName)
            {
                bool isKind = copy.ValueTextEquals("kind");
                copy.Read();

                if (isKind)
                {
                    return copy.GetString() == "circle" ? typeof(Disc) : typeof(Group);
                }

                copy.Skip();
                copy.Read();
            }

            return null;
        };
}

#pragma warning restore CA1815

public sealed record Tagged(IReadOnlySet<string> Tags);

public sealed class ReadOnlyExtra
{
    public string Name { get; set; } = "";

    [JsonExtensionData]
    public IReadOnlyDictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class NodeExtra
{
    public string Name { get; set; } = "";

    [JsonExtensionData]
    public JsonObject? Extra { get; set; }
}

public sealed class PerMemberNames
{
    [JsonNamingPolicy(JsonKnownNamingPolicy.SnakeCaseUpper)]
    public string FirstName { get; set; } = "";

    public string LastName { get; set; } = "";
}

public closed record class Gate;

public sealed record class Shut : Gate;

public sealed record class Ajar(float Percent) : Gate;

public sealed record GateHolder(Gate Gate);

/// <summary>C# 15 unions and the number types that System.Text.Json 11 reads.</summary>
public sealed class Net11TypesTests
{
    private static readonly JsonSerializerOptions Plain = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    private static readonly JsonSerializerOptions Structural = Structure();

    private static JsonSerializerOptions Structure()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        options.TypeClassifiers.Add(new JsonUnionTypeStructuralClassifier());
        return options;
    }

    private static T? Serializer<T>(string json, JsonSerializerOptions options)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, options);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    [Fact]
    public void A_union_is_any_of_its_cases()
    {
        JsonObject pet = Schema.Object<PetOwner>(Structural).ToJsonSchema()["properties"]!["pet"]!.AsObject();

        JsonArray cases = pet["anyOf"]!.AsArray();
        Assert.Equal(3, cases.Count);
        Assert.Equal("null", cases[2]!["type"]!.GetValue<string>());
        Assert.Equal(["name", "lives"], cases[0]!["properties"]!.AsObject().Select(property => property.Key));
        Assert.Equal(["name", "good"], cases[1]!["properties"]!.AsObject().Select(property => property.Key));
    }

    [Theory]
    [InlineData("""{"pet":{"name":"Tom","lives":3}}""")]
    [InlineData("""{"pet":{"name":"Rex","good":true}}""")]
    [InlineData("""{"pet":{"name":"Rex","good":true,"extra":1}}""")]
    [InlineData("""{"pet":{"name":"Rex"}}""")]
    [InlineData("""{"pet":42}""")]
    [InlineData("""{"pet":null}""")]
    public void A_union_of_objects_parses_as_the_serializer_reads_it(string json)
    {
        ObjectSchema<PetOwner> schema = Schema.Object<PetOwner>(Structural);

        ParseResult<PetOwner> parsed = schema.SafeParse(json);
        PetOwner? expected = Serializer<PetOwner>(json, Structural);

        // Where parsing accepts, it gives what the serializer gives, because the union reaches it as written.
        if (parsed.IsSuccess)
        {
            Assert.NotNull(expected);
            Assert.Equal(JsonSerializer.Serialize(expected, Structural), JsonSerializer.Serialize(parsed.Value, Structural));
        }

        // And it never accepts what the input schema refuses.
        Assert.True(!parsed.IsSuccess || IsValid(schema.ToJsonSchema(), json), json);
    }

    [Fact]
    public void A_case_keeps_the_unknown_properties_the_classifier_reads()
    {
        ObjectSchema<PetOwner> schema = Schema.Object<PetOwner>(Structural);

        // With "good" stripped, the structural classifier couldn't tell Puppy from Kitten any more.
        PetOwner owner = schema.Parse("""{"pet":{"name":"Rex","good":true}}""");

        Assert.Equal("Rex", Assert.IsType<Puppy>(owner.Pet.Value).Name);
    }

    [Fact]
    public void Two_object_cases_without_a_classifier_fail_as_the_serializer_does()
    {
        ParseResult<PetOwner> parsed = Schema.Object<PetOwner>(new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() })
            .SafeParse("""{"pet":{"name":"Tom","lives":3}}""");

        Assert.False(parsed.IsSuccess);
        Assert.Equal(IssueCodes.DeserializationFailed, Assert.Single(parsed.Issues).Code);
    }

    [Theory]
    [InlineData("""{"Value":"text"}""", true)]
    [InlineData("""{"Value":5}""", true)]
    [InlineData("""{"Value":true}""", false)]
    [InlineData("""{"Value":null}""", true)]
    public void A_union_of_scalars_takes_each_case(string json, bool valid)
    {
        ObjectSchema<ScalarHolder> schema = Schema.Object<ScalarHolder>(Plain);

        Assert.Equal(valid, schema.SafeParse(json).IsSuccess);
        Assert.Equal(valid, IsValid(schema.ToJsonSchema(), json));
    }

    [Fact]
    public void A_nullable_case_allows_null()
    {
        ObjectSchema<MaybeTextHolder> schema = Schema.Object<MaybeTextHolder>(Plain);

        Assert.True(schema.SafeParse("""{"Value":null}""").IsSuccess);
        Assert.True(IsValid(schema.ToJsonSchema(), """{"Value":null}"""));
        Assert.False(IsValid(schema.ToJsonSchema(), """{"Value":true}"""));
    }

    [Fact]
    public void A_case_that_takes_anything_makes_the_union_take_anything()
    {
        JsonNode? value = Schema.Object<LooseHolder>(Plain).ToJsonSchema()["properties"]!["Value"];

        Assert.Empty(value!.AsObject());
    }

    [Fact]
    public void Validate_checks_a_union_value()
    {
        ObjectSchema<PetOwner> schema = Schema.Object<PetOwner>(Structural);

        Assert.True(schema.Validate(new PetOwner(new Puppy("Rex", Good: true))).IsSuccess);
    }

    [Fact]
    public void The_new_number_types_are_numbers()
    {
        JsonObject properties = Schema.Object<Measurements>(Plain).ToJsonSchema()["properties"]!.AsObject();

        foreach (string name in new[] { "Brain", "Small", "Medium", "Large" })
        {
            Assert.Equal("number", properties[name]!["type"]!.GetValue<string>());
        }

        Assert.Null(properties["Large"]!["maximum"]);
    }

    [Fact]
    public void BFloat16_takes_its_largest_value_as_the_serializer_writes_it()
    {
        // The serializer writes BFloat16.MaxValue as 3.39E+38, which is above the exact value, so no range is stated, as for float.
        string written = JsonSerializer.Serialize(BFloat16.MaxValue);

        Assert.True(Z.Number<BFloat16>().SafeParse(written).IsSuccess, written);
        Assert.True(Z.Number<BFloat16>().Validate(BFloat16.MaxValue).IsSuccess);
        Assert.True(Z.Number<BFloat16>().Validate(BFloat16.MinValue).IsSuccess);
        Assert.False(Z.Number<BFloat16>().SafeParse("4e38").IsSuccess);
        Assert.Null(Z.Number<BFloat16>().ToJsonSchema()["maximum"]);
    }

    [Theory]
    [InlineData("1.5", true)]
    [InlineData("-0.000001", true)]
    [InlineData("1e7000", false)]
    [InlineData("\"1\"", false)]
    public void Decimal128_parses_what_fits(string json, bool valid) =>
        Assert.Equal(valid, Z.Number<Decimal128>().SafeParse(json).IsSuccess);

    [Fact]
    public void The_new_number_types_round_trip_through_a_typed_schema()
    {
        ObjectSchema<Measurements> schema = Schema.Object<Measurements>(Plain);

        Measurements value = schema.Parse("""{"Brain":2.5,"Small":1.25,"Medium":10.5,"Large":123.456,"Maybe":null}""");

        Assert.Equal((BFloat16)2.5f, value.Brain);
        Assert.Equal(Decimal128.Parse("123.456", System.Globalization.CultureInfo.InvariantCulture), value.Large);
        Assert.Null(value.Maybe);
    }

    [Fact]
    public void Nothing_inside_a_union_is_removed_before_the_serializer_picks_the_case()
    {
        ObjectSchema<Litters> schema = Schema.Object<Litters>(Structural);
        const string Json = """{"one":{"name":"r","good":true,"data":{"x":1,"y":2}},"many":[{"name":"r","good":true,"data":{"x":1,"y":2}}]}""";

        // The Kit case also matches, and its Box would have dropped "y" though the serializer picks Pup.
        Litters parsed = schema.Parse(Json);

        Assert.Equal(2, Assert.IsType<Pup>(parsed.One.Value).Data.Y);
        Assert.Equal(JsonSerializer.Serialize(JsonSerializer.Deserialize<Litters>(Json, Structural), Structural), JsonSerializer.Serialize(parsed, Structural));
    }

    [Theory]
    [InlineData("""{"figure":{"kind":"group","name":"g","child":{"kind":"circle","r":1}}}""")]
    [InlineData("""{"figure":{"kind":"group","name":"g","child":{"kind":"group","name":"h","child":{"kind":"circle","r":1}}}}""")]
    public void A_recursive_union_keeps_what_its_classifier_reads_at_every_level(string json)
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        ParseResult<Drawing> parsed = Schema.Object<Drawing>(options).SafeParse(json);

        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Issues.Select(issue => issue.Message)));
        Assert.Equal(JsonSerializer.Serialize(JsonSerializer.Deserialize<Drawing>(json, options), options), JsonSerializer.Serialize(parsed.Value, options));
    }

    [Fact]
    public void An_unset_union_is_null_in_and_out_as_the_serializer_has_it()
    {
        ObjectSchema<Unset> schema = Schema.Object<Unset>(Plain);
        Unset unset = schema.Parse("{}");

        Assert.Null(unset.Value.Value);
        Assert.True(schema.Validate(unset).IsSuccess);
        Assert.True(schema.SafeParse("""{"Value":null}""").IsSuccess);
        Assert.True(IsValid(schema.ToJsonSchema(new() { Direction = Zorg.Json.Emission.SchemaDirection.Output }), JsonSerializer.Serialize(unset, Plain)));
    }

    [Fact]
    public void A_union_allows_null_once()
    {
        JsonSerializerOptions options = Structure();
        JsonObject properties = Schema.Object<Shapes>(options).ToJsonSchema()["properties"]!.AsObject();

        // Pet? doesn't add a second null.
        Assert.Equal(["object", "object", "null"], properties["maybe"]!["anyOf"]!.AsArray().Select(branch => branch!["type"]!.GetValue<string>()));

        // A union case that is a union brings its own cases. The structural classifier refuses that shape, so this uses the default one.
        JsonNode? wide = Schema.Object<WideHolder>(Plain).ToJsonSchema()["properties"]!["Wide"];
        Assert.Equal(["string", "integer", "boolean", "null"], wide!["anyOf"]!.AsArray().Select(branch => branch!["type"]!.GetValue<string>()));

        // Optional<Pet> can't read null, but an Optional<Pet> holding an unset union is written as null, so the schema keeps it.
        Assert.Single(properties["later"]!["anyOf"]!.AsArray(), branch => branch!["type"]?.GetValue<string>() == "null");
        Assert.False(Schema.Object<Shapes>(options).SafeParse("""{"maybe":null,"later":null}""").IsSuccess);
        Assert.True(Schema.Object<Shapes>(options).SafeParse("""{"maybe":null,"later":{"name":"Tom","lives":1}}""").IsSuccess);
    }

    [Fact]
    public void A_read_only_set_is_an_array()
    {
        ObjectSchema<Tagged> schema = Schema.Object<Tagged>(Plain);

        Assert.Equal("array", schema.ToJsonSchema()["properties"]!["Tags"]!["type"]!.GetValue<string>());
        Assert.Equal(["a", "b"], schema.Parse("""{"Tags":["a","b"]}""").Tags.Order());
    }

    [Fact]
    public void Read_only_and_JsonObject_extension_data_keep_unknown_properties()
    {
        Assert.Equal(1, Schema.Object<ReadOnlyExtra>(Plain).Parse("""{"Name":"n","other":1}""").Extra!["other"].GetInt32());
        Assert.Equal(1, Schema.Object<NodeExtra>(Plain).Parse("""{"Name":"n","other":1}""").Extra!["other"]!.GetValue<int>());
    }

    [Fact]
    public void A_naming_policy_on_a_member_names_its_property()
    {
        JsonObject properties = Schema.Object<PerMemberNames>(Plain).ToJsonSchema()["properties"]!.AsObject();

        Assert.Equal(["FIRST_NAME", "LastName"], properties.Select(property => property.Key));
    }

    [Fact]
    public void An_inferred_closed_hierarchy_is_a_discriminated_union()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver(), InferClosedTypePolymorphism = true };
        ObjectSchema<GateHolder> schema = Schema.Object<GateHolder>(options);

        Assert.Equal(2, schema.ToJsonSchema()["properties"]!["gate"]!["oneOf"]!.AsArray().Count);
        Assert.Equal(10f, Assert.IsType<Ajar>(schema.Parse("""{"gate":{"$type":"Ajar","percent":10}}""").Gate).Percent);
        Assert.False(schema.SafeParse("""{"gate":{"$type":"Nope"}}""").IsSuccess);
    }

    // The independent check against JsonSchema.Net is in StructuredGuaranteeTests. This one reads the schema back with Zorg's importer.
    private static bool IsValid(JsonObject schema, string json) =>
        Z.FromJsonSchema(schema.ToJsonString()).SafeParse(json).IsSuccess;
}
#endif
