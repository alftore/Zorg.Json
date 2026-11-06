using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Xunit;

namespace Zorg.Json.Tests;

public sealed class PersonPatch
{
    [JsonConverter(typeof(OptionalJsonConverter<string>))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<string> Name { get; init; }

    [JsonConverter(typeof(OptionalJsonConverter<string?>))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<string?> Nick { get; init; }

    [JsonConverter(typeof(OptionalJsonConverter<int>))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<int> Age { get; init; }

    [JsonConverter(typeof(OptionalJsonConverter<int?>))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<int?> Height { get; init; }

    [JsonConverter(typeof(OptionalJsonConverter<Address>))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<Address> Home { get; init; }

    [JsonConverter(typeof(OptionalJsonConverter<Address?>))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<Address?> Work { get; init; }
}

/// <summary>Reads through a constructor, where an absent argument is <c>default</c>.</summary>
public sealed record PatchRecord(
    [property: JsonConverter(typeof(OptionalJsonConverter<string>))]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    Optional<string> Title);

/// <summary>Has no attributes at all, for the reflection path.</summary>
public sealed class PlainPatch
{
    public Optional<string> Name { get; init; }

    public Optional<int> Age { get; init; }
}

[JsonSerializable(typeof(PersonPatch))]
[JsonSerializable(typeof(PatchRecord))]
[JsonSerializable(typeof(Address))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(int?))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
public sealed partial class PatchJson : JsonSerializerContext;

public sealed class OptionalTests
{
    private static readonly JsonSerializerOptions Bare = new() { Converters = { new OptionalJsonConverterFactory() } };

    private static readonly ObjectSchema<Address> AddressSchema = Schema.Object(TestJson.Default.Address)
        .Property(a => a.Street, s => s.MinLength(1));

    private static readonly ObjectSchema<PersonPatch> PatchSchema = Schema.Object(PatchJson.Default.PersonPatch)
        .Property(p => p.Name, s => s.MinLength(1).MaxLength(10))
        .Property(p => p.Nick, s => s.MaxLength(5))
        .Property(p => p.Age, s => s.Minimum(0).Maximum(150))
        .Property(p => p.Height, s => s.Minimum(0))
        .Property(p => p.Home, AddressSchema)
        .Property(p => p.Work, (Schema<Address?>)AddressSchema!);

    [Fact]
    public void An_absent_property_is_unset()
    {
        PersonPatch patch = PatchSchema.Parse("{}");

        Assert.False(patch.Name.IsSet);
        Assert.False(patch.Nick.IsSet);
        Assert.False(patch.Age.IsSet);
        Assert.False(patch.Home.IsSet);
        Assert.Throws<System.InvalidOperationException>(() => patch.Name.Value);
    }

    [Fact]
    public void Null_is_a_set_value_unlike_absence()
    {
        PersonPatch patch = PatchSchema.Parse("""{ "nick": null, "height": null }""");

        Assert.True(patch.Nick.IsSet);
        Assert.Null(patch.Nick.Value);
        Assert.True(patch.Height.IsSet);
        Assert.Null(patch.Height.Value);
        Assert.False(patch.Name.IsSet);
    }

    [Fact]
    public void Values_are_read()
    {
        PersonPatch patch = PatchSchema.Parse("""{ "name": "Ada", "age": 36, "home": { "street": "A" } }""");

        Assert.Equal("Ada", patch.Name.Value);
        Assert.Equal(36, patch.Age.Value);
        Assert.Equal("A", patch.Home.Value.Street);
    }

    [Fact]
    public void A_constructor_argument_is_unset_when_absent()
    {
        Schema<PatchRecord> schema = Schema.Object(PatchJson.Default.PatchRecord);

        Assert.False(schema.Parse("{}").Title.IsSet);
        Assert.Equal("t", schema.Parse("""{ "title": "t" }""").Title.Value);
    }

    [Fact]
    public void Nothing_is_required()
    {
        JsonObject schema = PatchSchema.ToJsonSchema();

        Assert.False(schema.ContainsKey("required"));
        Assert.Equal(6, schema["properties"]!.AsObject().Count);
    }

    [Fact]
    public void Properties_have_the_schema_of_their_value()
    {
        JsonObject properties = PatchSchema.ToJsonSchema()["properties"]!.AsObject();

        Assert.Equal("string", properties["name"]!["type"]!.GetValue<string>());
        Assert.Equal(1, properties["name"]!["minLength"]!.GetValue<int>());
        Assert.Equal(5, properties["nick"]!["maxLength"]!.GetValue<int>());
        Assert.Equal(0, properties["age"]!["minimum"]!.GetValue<int>());
        Assert.Equal(0, properties["height"]!["minimum"]!.GetValue<int>());
        Assert.Equal(1, properties["home"]!["properties"]!["street"]!["minLength"]!.GetValue<int>());
    }

    [Fact]
    public void Null_is_only_allowed_where_the_value_type_is_nullable()
    {
        Assert.False(PatchSchema.SafeParse("""{ "name": null }""").IsSuccess);
        Assert.False(PatchSchema.SafeParse("""{ "age": null }""").IsSuccess);
        Assert.False(PatchSchema.SafeParse("""{ "home": null }""").IsSuccess);
        Assert.True(PatchSchema.SafeParse("""{ "nick": null }""").IsSuccess);
        Assert.True(PatchSchema.SafeParse("""{ "height": null }""").IsSuccess);
        Assert.True(PatchSchema.SafeParse("""{ "work": null }""").IsSuccess);

        JsonObject properties = PatchSchema.ToJsonSchema()["properties"]!.AsObject();

        Assert.Equal("""["string","null"]""", properties["nick"]!["type"]!.ToJsonString());
        Assert.Equal("string", properties["name"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Rules_apply_to_set_values()
    {
        ParseResult<PersonPatch> result = PatchSchema.SafeParse("""{ "name": "", "age": 200, "nick": "toolong", "home": { "street": "" }, "height": -1 }""");

        Assert.Equal(5, result.Issues.Length);
        Assert.Contains(result.Issues, issue => issue.InstanceLocation == "/home/street");
    }

    [Fact]
    public void Wrong_types_are_issues_and_never_throw()
    {
        Assert.Equal(IssueCodes.InvalidType, Assert.Single(PatchSchema.SafeParse("""{ "name": 5 }""").Issues).Code);
        Assert.False(PatchSchema.SafeParse("""{ "age": "x" }""").IsSuccess);
        Assert.False(PatchSchema.Strict().SafeParse("""{ "extra": 1 }""").IsSuccess);
        Assert.False(PatchSchema.SafeParse("[]").IsSuccess);
    }

    [Fact]
    public void An_unset_value_is_left_out_when_serializing()
    {
        PersonPatch patch = new() { Name = "Ada", Nick = new Optional<string?>(null) };

        string json = JsonSerializer.Serialize(patch, PatchJson.Default.PersonPatch);

        Assert.Equal("""{"name":"Ada","nick":null}""", json);
        Assert.Equal("{}", JsonSerializer.Serialize(new PersonPatch(), PatchJson.Default.PersonPatch));
    }

    [Fact]
    public void A_set_default_value_is_not_left_out()
    {
        PersonPatch patch = new() { Age = 0 };

        Assert.Equal("""{"age":0}""", JsonSerializer.Serialize(patch, PatchJson.Default.PersonPatch));
    }

    [Fact]
    public void Round_trips_through_validate()
    {
        PersonPatch patch = new() { Name = "Ada", Home = new Address("A") };

        ParseResult<PersonPatch> result = PatchSchema.Validate(patch);

        Assert.True(result.IsSuccess);
        Assert.Equal("Ada", result.Value.Name.Value);
        Assert.False(result.Value.Age.IsSet);
        Assert.False(PatchSchema.Validate(new PersonPatch { Name = "" }).IsSuccess);
    }

    [Fact]
    public void The_factory_serves_reflection_based_options()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            Converters = { new OptionalJsonConverterFactory() },
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { OptionalJson.OmitUnset } },
        };

        PlainPatch patch = JsonSerializer.Deserialize<PlainPatch>("""{ "name": "Ada" }""", options)!;

        Assert.Equal("Ada", patch.Name.Value);
        Assert.False(patch.Age.IsSet);
        Assert.Equal("""{"name":"Ada"}""", JsonSerializer.Serialize(patch, options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PlainPatch>("""{ "age": null }""", options));
    }

    [Fact]
    public void Without_omit_an_unset_value_is_written_as_null()
    {
        Assert.Equal("""{"Name":"Ada","Age":null}""", JsonSerializer.Serialize(new PlainPatch { Name = "Ada" }, Bare));
    }

    [Fact]
    public void It_compares_and_converts()
    {
        Optional<string> name = "Ada";

        Assert.True(name.IsSet);
        Assert.Equal(new Optional<string>("Ada"), name);
        Assert.NotEqual(default, name);
        Assert.True(default(Optional<int>) == Optional<int>.Unset);
        Assert.True(name.TryGetValue(out string? value) && value == "Ada");
        Assert.Equal("x", default(Optional<string>).GetValueOrDefault("x"));
        Assert.Equal("unset", default(Optional<int>).ToString());
    }

    [Fact]
    public void Property_inference_works_for_the_plain_types_too()
    {
        // Overload resolution: the same call shapes must still pick the plain overloads.
        ObjectSchema<Person> person = Schema.Object(TestJson.Default.Person)
            .Property(p => p.Name, s => s.MinLength(1))
            .Property(p => p.Age, s => s.Minimum(0))
            .Property(p => p.Height, s => s.Minimum(0))
            .Property(p => p.Active, s => s.Const(true))
            .Property(p => p.Home, AddressSchema);

        Assert.False(person.SafeParse("""{ "name": "" }""").IsSuccess);
    }
}
