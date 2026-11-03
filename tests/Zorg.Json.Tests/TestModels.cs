using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zorg.Json.Tests;

public sealed record Address(string Street, string? Zip = null);

public enum Color
{
    Red,
    DarkBlue,
}

public sealed record Person(string Name, string? Nick, int Age, int? Height, decimal Salary, bool Active, List<Address> Addresses, IReadOnlyList<string> Tags, Address Home)
{
    [JsonPropertyName("other")]
    public string Renamed { get; init; } = string.Empty;

    public required string MustHave { get; init; }

    public Guid? Id { get; init; }

    public string Computed => Name + "!";
}

public sealed record Category(string Name, List<Category> Children);

public abstract record Shape;

public sealed record Circle(double Radius) : Shape;

public sealed record Square(double Side) : Shape;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(Cat), "cat")]
[JsonDerivedType(typeof(Dog), "dog")]
public abstract record Animal;

public sealed record Cat(string Name) : Animal;

public sealed record Dog(int Barks) : Animal;

public sealed record Palette(Color Main, Dictionary<string, int> Weights);

public sealed class Open
{
    public required string Name { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

[JsonSerializable(typeof(Person))]
[JsonSerializable(typeof(Category))]
[JsonSerializable(typeof(Circle))]
[JsonSerializable(typeof(Square))]
[JsonSerializable(typeof(Animal))]
[JsonSerializable(typeof(Palette))]
[JsonSerializable(typeof(Open))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, Converters = [typeof(JsonStringEnumConverter<Color>)])]
public sealed partial class TestJson : JsonSerializerContext;
