using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Xunit;

namespace Zorg.Json.Tests;

public sealed class Loop
{
    public Loop? Next { get; set; }
}

[JsonSerializable(typeof(Loop))]
public sealed partial class ValidateJson : JsonSerializerContext;

public sealed class ValidateTests
{
    private static readonly ObjectSchema<Address> AddressSchema = Schema.Object(TestJson.Default.Address)
        .Property(a => a.Street, s => s.MinLength(1));

    private static readonly ObjectSchema<Person> PersonSchema = Schema.Object(TestJson.Default.Person)
        .Property(p => p.Name, s => s.MinLength(1).MaxLength(10))
        .Property(p => p.Age, s => s.Minimum(0).Maximum(150))
        .Property(p => p.Home, AddressSchema);

    private static Person Valid() => new("Ada", null, 36, null, 10.5m, true, [new Address("A")], ["x"], new Address("B"))
    {
        MustHave = "yes",
    };

    [Fact]
    public void A_valid_instance_succeeds_and_ignores_computed_properties()
    {
        ParseResult<Person> result = PersonSchema.Validate(Valid());

        Assert.True(result.IsSuccess, string.Join(';', result.Issues.ToPrettyString()));
        Assert.Equal("Ada", result.Value.Name);
        Assert.Equal("Ada!", result.Value.Computed);
    }

    [Fact]
    public void Issues_point_into_the_serialized_instance()
    {
        Person person = Valid() with { Age = 200, Home = new Address("") };

        ParseResult<Person> result = PersonSchema.Validate(person);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Issues, issue => issue.InstanceLocation == "/age" && issue.Code == IssueCodes.TooBig);
        Assert.Contains(result.Issues, issue => issue.InstanceLocation == "/home/street" && issue.Code == IssueCodes.TooSmall);
    }

    [Fact]
    public void The_result_is_the_value_after_normalizers()
    {
        ParseResult<string> result = Z.String().Trim().MinLength(1).Validate("  ada  ");

        Assert.Equal("ada", result.Value);
        Assert.False(Z.String().Trim().MinLength(1).Validate("   ").IsSuccess);
    }

    [Fact]
    public void The_result_has_defaults_filled_in()
    {
        ObjectSchema schema = Z.Object().Property("limit", Z.Integer().Default(10));

        ParseResult<JsonElement> result = schema.Validate(JsonElement.Parse("{}"));

        Assert.Equal(10, result.Value.GetProperty("limit").GetInt32());
    }

    [Fact]
    public void The_instance_is_not_changed()
    {
        Person person = Valid();

        ParseResult<Person> result = PersonSchema.Validate(person);

        Assert.NotSame(person, result.Value);
        Assert.Equal("Ada", person.Name);
    }

    [Fact]
    public void Null_is_validated_as_json_null()
    {
        ParseResult<string> rejected = Z.String().Validate(null!);
        ParseResult<string> allowed = Z.String().Nullable().Validate(null!);
        ParseResult<Person> person = PersonSchema.Validate(null!);

        Assert.Equal(IssueCodes.InvalidType, Assert.Single(rejected.Issues).Code);
        Assert.True(allowed.IsSuccess);
        Assert.False(person.IsSuccess);
    }

    [Fact]
    public void Serializer_failures_are_issues()
    {
        Loop loop = new();
        loop.Next = loop;

        ParseResult<Loop> result = Schema.Object(ValidateJson.Default.Loop).Validate(loop);

        Assert.Equal(IssueCodes.DeserializationFailed, Assert.Single(result.Issues).Code);
    }

    [Fact]
    public void Refinements_run_on_the_converted_value()
    {
        ObjectSchema<Person> schema = PersonSchema.Refine(p => p.Age >= 18 || p.Nick is null, "Minors cannot have a nickname", p => p.Nick);

        Assert.True(schema.Validate(Valid()).IsSuccess);

        ParseResult<Person> result = schema.Validate(Valid() with { Age = 10, Nick = "ada" });

        Assert.Equal("/nick", Assert.Single(result.Issues).InstanceLocation);
    }

    [Fact]
    public async Task Async_refinements_need_the_async_method()
    {
        StringSchema schema = Z.String().RefineAsync((name, _) => ValueTask.FromResult(name != "admin"), "Taken");

        Assert.Throws<System.InvalidOperationException>(() => schema.Validate("ada"));
        Assert.True((await schema.ValidateAsync("ada", TestContext.Current.CancellationToken)).IsSuccess);
        Assert.False((await schema.ValidateAsync("admin", TestContext.Current.CancellationToken)).IsSuccess);
    }
}
