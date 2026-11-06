using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Zorg.Json.Emission;

namespace Zorg.Json.Tests;

public sealed class TypedObjectSchemaTests
{
    private const string Valid = """
        {
          "name": "Ada", "nick": null, "age": 36, "height": null, "salary": 10.5, "active": true,
          "addresses": [{ "street": "A" }], "tags": ["x"], "home": { "street": "B", "zip": "1" },
          "mustHave": "yes"
        }
        """;

    private static readonly ObjectSchema<Address> AddressSchema = Schema.Object(TestJson.Default.Address)
        .Property(a => a.Street, s => s.MinLength(1));

    private static readonly ObjectSchema<Person> PersonSchema = Schema.Object(TestJson.Default.Person)
        .Property(p => p.Name, s => s.MinLength(1).MaxLength(10))
        .Property(p => p.Age, s => s.Minimum(0).Maximum(150))
        .Property(p => p.Height, s => s.Minimum(0))
        .Property(p => p.Salary, s => s.MultipleOf(0.5m))
        .Property(p => p.Addresses, s => s.MinItems(1).Items(AddressSchema))
        .Property(p => p.Home, AddressSchema);

    private static string Change(string name, string json)
    {
        JsonObject document = JsonNode.Parse(Valid)!.AsObject();
        document[name] = JsonNode.Parse(json);
        return document.ToJsonString();
    }

    private static string Remove(string name)
    {
        JsonObject document = JsonNode.Parse(Valid)!.AsObject();
        document.Remove(name);
        return document.ToJsonString();
    }

    private static ParseIssue SingleIssue<T>(ParseResult<T> result) => Assert.Single(result.Issues);

    [Fact]
    public void Parses_into_the_type()
    {
        Person person = PersonSchema.Parse(Valid);

        Assert.Equal("Ada", person.Name);
        Assert.Equal(36, person.Age);
        Assert.Equal("B", person.Home.Street);
        Assert.Equal("A", Assert.Single(person.Addresses).Street);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("age")]
    [InlineData("mustHave")]
    [InlineData("nick")]
    public void Constructor_parameters_and_required_members_are_required(string property)
    {
        ParseIssue issue = SingleIssue(PersonSchema.SafeParse(Remove(property)));

        Assert.Equal(IssueCodes.Required, issue.Code);
        Assert.Equal("/" + property, issue.InstanceLocation);
    }

    [Theory]
    [InlineData("other")]
    [InlineData("id")]
    public void Init_only_properties_are_optional(string property) =>
        Assert.True(PersonSchema.SafeParse(Valid).IsSuccess && !PersonSchema.ToNode().Required.Contains(property));

    [Fact]
    public void Parameter_with_a_default_is_optional() => Assert.True(AddressSchema.SafeParse("""{ "street": "A" }""").IsSuccess);

    [Fact]
    public void Nullability_follows_the_declaration()
    {
        Assert.True(PersonSchema.SafeParse(Change("nick", "null")).IsSuccess);
        Assert.True(PersonSchema.SafeParse(Change("height", "null")).IsSuccess);
        Assert.Equal(IssueCodes.InvalidType, SingleIssue(PersonSchema.SafeParse(Change("name", "null"))).Code);
        Assert.Equal(IssueCodes.InvalidType, SingleIssue(PersonSchema.SafeParse(Change("age", "null"))).Code);
    }

    [Fact]
    public void Unconfigured_properties_are_still_validated()
    {
        ParseIssue issue = SingleIssue(PersonSchema.SafeParse(Change("tags", "[1]")));

        Assert.Equal(IssueCodes.InvalidType, issue.Code);
        Assert.Equal("/tags/0", issue.InstanceLocation);
    }

    [Fact]
    public void Unknown_properties_are_removed_by_default()
    {
        Assert.True(PersonSchema.SafeParse(Change("unknown", "1")).IsSuccess);
        Assert.Null(PersonSchema.ToJsonSchema()["additionalProperties"]);
        Assert.Null(PersonSchema.ToJsonSchema(new() { Direction = SchemaDirection.Output })["additionalProperties"]);
    }

    [Fact]
    public void Strict_rejects_unknown_properties()
    {
        ParseIssue issue = SingleIssue(PersonSchema.Strict().SafeParse(Change("unknown", "1")));

        Assert.Equal(IssueCodes.UnrecognizedKeys, issue.Code);
        Assert.Equal(["unknown"], (string[])issue.Parameters["keys"]!);
    }

    [Fact]
    public void Strip_removes_unknown_properties_before_deserializing() =>
        Assert.Equal("Ada", PersonSchema.Strip().Parse(Change("unknown", "1")).Name);

    [Fact]
    public void Output_only_properties_are_not_accepted() =>
        Assert.Equal(IssueCodes.UnrecognizedKeys, SingleIssue(PersonSchema.Strict().SafeParse(Change("computed", "\"x\""))).Code);

    [Fact]
    public void Json_names_come_from_the_contract() =>
        Assert.Equal("x", PersonSchema.Parse(Change("other", "\"x\"")).Renamed);

    [Fact]
    public void Issues_have_json_pointer_paths()
    {
        ParseIssue issue = SingleIssue(PersonSchema.SafeParse(Change("addresses", """[{ "street": "" }]""")));

        Assert.Equal(IssueCodes.TooSmall, issue.Code);
        Assert.Equal("/addresses/0/street", issue.InstanceLocation);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1e2")]
    [InlineData("2147483648")]
    public void Integers_that_cannot_deserialize_are_rejected(string json) =>
        Assert.Equal(IssueCodes.InvalidType, SingleIssue(PersonSchema.SafeParse(Change("age", json))).Code);

    [Fact]
    public void Property_rules_can_be_added_to_a_nullable_number() =>
        Assert.Equal(IssueCodes.TooSmall, SingleIssue(PersonSchema.SafeParse(Change("height", "-1"))).Code);

    [Fact]
    public void Refine_with_path_reports_at_the_property()
    {
        ObjectSchema<Person> schema = PersonSchema.Refine(p => p.Age >= 40, "Too young", p => p.Age);

        ParseIssue issue = SingleIssue(schema.SafeParse(Valid));

        Assert.Equal(IssueCodes.Custom, issue.Code);
        Assert.Equal("/age", issue.InstanceLocation);
    }

    [Fact]
    public void Optional_and_Omit_change_the_contract()
    {
        Assert.True(PersonSchema.Optional(p => p.Nick).SafeParse(Remove("nick")).IsSuccess);
        Assert.Equal(IssueCodes.UnrecognizedKeys, SingleIssue(PersonSchema.Strict().Omit(p => p.Id).SafeParse(Change("id", "null"))).Code);
    }

    [Fact]
    public void Invalid_member_expressions_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => PersonSchema.Property(p => p.Home.Street, s => s));
        Assert.Throws<ArgumentException>(() => PersonSchema.Omit(p => p.Id).Optional(p => p.Id));
    }

    [Fact]
    public void Reflection_overload_uses_web_defaults() =>
        Assert.Equal("Ada", Schema.Object<Address>().Parse("""{ "street": "Ada" }""").Street);

    [Fact]
    public void Enums_follow_the_converter()
    {
        ObjectSchema<Palette> schema = Schema.Object(TestJson.Default.Palette);

        Assert.Equal(Color.DarkBlue, schema.Parse("""{ "main": "DarkBlue", "weights": { "a": 1 } }""").Main);
        Assert.Equal(IssueCodes.InvalidValue, SingleIssue(schema.SafeParse("""{ "main": "Green", "weights": {} }""")).Code);
        Assert.Equal(IssueCodes.InvalidType, SingleIssue(schema.SafeParse("""{ "main": "Red", "weights": { "a": "x" } }""")).Code);
    }

    [Fact]
    public void Extension_data_allows_unknown_properties() =>
        Assert.True(Schema.Object(TestJson.Default.Open).SafeParse("""{ "name": "a", "b": 1 }""").IsSuccess);

    [Fact]
    public void Recursive_types_are_seeded_with_a_reference()
    {
        ObjectSchema<Category> schema = Schema.Object(TestJson.Default.Category);

        Category parsed = schema.Parse("""{ "name": "a", "children": [{ "name": "b", "children": [] }] }""");

        Assert.Equal("b", Assert.Single(parsed.Children).Name);
        Assert.Equal("/children/0/children/0", SingleIssue(schema.SafeParse("""{ "name": "a", "children": [{ "name": "b", "children": [1] }] }""")).InstanceLocation);
        Snapshot.Match(schema.ToJsonSchema().ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void JsonPolymorphic_hierarchies_become_a_discriminated_union()
    {
        ObjectSchema<Animal> schema = Schema.Object(TestJson.Default.Animal);

        Assert.IsType<Cat>(schema.Parse("""{ "$type": "cat", "name": "Tom" }"""));
        Assert.IsType<Dog>(schema.Parse("""{ "$type": "dog", "barks": 2 }"""));
        Assert.Equal(IssueCodes.InvalidUnion, SingleIssue(schema.SafeParse("""{ "$type": "cow" }""")).Code);
        Assert.Equal("/name", SingleIssue(schema.SafeParse("""{ "$type": "cat" }""")).InstanceLocation);
    }

    [Fact]
    public void Emitted_schema_matches_the_contract() =>
        Snapshot.Match(PersonSchema.ToJsonSchema().ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    [Fact]
    public void Output_schema_includes_output_only_properties()
    {
        JsonObject properties = PersonSchema.ToJsonSchema(new() { Direction = SchemaDirection.Output })["properties"]!.AsObject();

        Assert.True(properties.ContainsKey("computed"));
        Assert.False(PersonSchema.ToJsonSchema()["properties"]!.AsObject().ContainsKey("computed"));
    }

    [Fact]
    public void Union_selects_the_case_by_discriminator()
    {
        Schema<Shape> schema = Schema.Union<Shape>("kind")
            .Case("circle", Schema.Object(TestJson.Default.Circle).Property(c => c.Radius, s => s.Positive()))
            .Case("square", Schema.Object(TestJson.Default.Square));

        Assert.Equal(new Circle(2), schema.Parse("""{ "kind": "circle", "radius": 2 }"""));
        Assert.Equal(new Square(3), schema.Parse("""{ "kind": "square", "side": 3 }"""));

        ParseIssue unknown = SingleIssue(schema.SafeParse("""{ "kind": "hexagon" }"""));
        Assert.Equal(IssueCodes.InvalidUnion, unknown.Code);
        Assert.Equal("/kind", unknown.InstanceLocation);
        Assert.Equal("/radius", SingleIssue(schema.SafeParse("""{ "kind": "circle", "radius": 0 }""")).InstanceLocation);
        Assert.Equal(2, schema.ToJsonSchema()["oneOf"]!.AsArray().Count);
    }

    [Fact]
    public void Lazy_builds_recursive_typed_schemas()
    {
        Assert.Equal("b", Assert.Single(LazyCategory.Schema.Parse("""{ "name": "b", "children": [{ "name": "x", "children": [] }] }""").Children).Name is "x" ? "b" : "?");
        Assert.Equal(IssueCodes.TooSmall, SingleIssue(LazyCategory.Schema.SafeParse("""{ "name": "b", "children": [{ "name": "", "children": [] }] }""")).Code);
        Assert.True(LazyCategory.Schema.ToJsonSchema().ContainsKey("$defs"));
    }

    [Fact]
    public void Parsable_parses_to_the_type()
    {
        ParsableSchema<Guid> schema = Schema.Parsable<Guid>("uuid");
        Guid id = Guid.NewGuid();

        Assert.Equal(id, schema.Parse($"\"{id}\""));
        Assert.False(schema.SafeParse("\"nope\"").IsSuccess);
        Assert.Equal("uuid", schema.ToJsonSchema()["format"]!.GetValue<string>());
    }

    private static class LazyCategory
    {
        public static readonly ObjectSchema<Category> Schema = Zorg.Json.Schema.Object(TestJson.Default.Category)
            .Property(c => c.Name, s => s.MinLength(1))
            .Property(c => c.Children, s => s.Items(Zorg.Json.Schema.Lazy<Category>("category", () => Schema!)));
    }
}
