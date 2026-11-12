#if NET11_0_OR_GREATER
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zorg.Json;

// A C# union compiles to a struct. These are sample data, compared through their JSON, so CA1815 doesn't apply.
#pragma warning disable CA1815
[JsonUnion(TypeClassifier = typeof(JsonUnionTypeStructuralClassifier))]
internal union SmokePet(SmokeCat, SmokeDog);

internal union SmokeTag(string, bool);
#pragma warning restore CA1815

internal sealed record SmokeCat(string Name, int Lives);

internal sealed record SmokeDog(string Name, bool Good);

internal sealed record SmokeHome(SmokePet Pet, SmokeTag Tag, BFloat16 Brain, Decimal128 Price);

[JsonSerializable(typeof(SmokeHome))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, NumberHandling = JsonNumberHandling.Strict)]
internal sealed partial class Net11SmokeJson : JsonSerializerContext;

/// <summary>What the .NET 11 build adds, through a source-generated contract: C# unions and the new number types.</summary>
internal static class Net11Smoke
{
    public static IEnumerable<(string Name, bool Passed)> Run()
    {
        ObjectSchema<SmokeHome> home = Schema.Object(Net11SmokeJson.Default.SmokeHome);

        ParseResult<SmokeHome> dog = home.SafeParse("""{"pet":{"name":"Rex","good":true},"tag":"x","brain":2.5,"price":19.99}""");
        yield return ("union of objects", dog.IsSuccess && dog.Value.Pet.Value is SmokeDog { Good: true });

        ParseResult<SmokeHome> cat = home.SafeParse("""{"pet":{"name":"Tom","lives":3},"tag":true,"brain":1,"price":1}""");
        yield return ("union of scalars", cat.IsSuccess && cat.Value.Pet.Value is SmokeCat && cat.Value.Tag.Value is true);

        yield return ("union refused", !home.SafeParse("""{"pet":1,"tag":"x","brain":1,"price":1}""").IsSuccess);
        yield return ("union schema", home.ToJsonSchema()["properties"]?["pet"]?["anyOf"]?.AsArray().Count == 3);
        yield return ("BFloat16 overflow", !home.SafeParse("""{"pet":{"name":"Tom","lives":3},"tag":"x","brain":1e39,"price":1}""").IsSuccess);
        yield return ("Decimal128", dog.IsSuccess && dog.Value.Price == Decimal128.Parse("19.99", System.Globalization.CultureInfo.InvariantCulture));
        yield return ("union validate", dog.IsSuccess && home.Validate(dog.Value).IsSuccess);
    }
}
#endif
