using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zorg.Json.Model;
using Zorg.Json.Validation;

namespace Zorg.Json;

/// <summary>
/// A schema for a C# enum, written as the name of a member. Create one with <see cref="Z.Enum{TEnum}"/>. Zod equivalent:
/// <c>z.enum()</c>, or <c>z.nativeEnum()</c> in Zod 3.
/// </summary>
/// <typeparam name="TEnum">The enum type.</typeparam>
public sealed class EnumSchema<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum> : Schema<TEnum, EnumSchema<TEnum>>
    where TEnum : struct, Enum
{
    private readonly FrozenDictionary<string, TEnum> _byName;
    private readonly FrozenDictionary<TEnum, string> _byValue;

    internal EnumSchema(SchemaNode node, FrozenDictionary<string, TEnum> byName, FrozenDictionary<TEnum, string> byValue)
        : base(node)
    {
        _byName = byName;
        _byValue = byValue;
    }

    internal static EnumSchema<TEnum> Create(JsonNamingPolicy? namingPolicy)
    {
        string[] fieldNames = Enum.GetNames<TEnum>();

        if (fieldNames.Length == 0)
        {
            throw new ArgumentException($"{typeof(TEnum).Name} has no members, so no value could pass.");
        }

        Dictionary<string, TEnum> byName = new(fieldNames.Length, StringComparer.Ordinal);
        Dictionary<TEnum, string> byValue = new(fieldNames.Length);
        ImmutableArray<JsonElement>.Builder elements = ImmutableArray.CreateBuilder<JsonElement>(fieldNames.Length);

        for (int index = 0; index < fieldNames.Length; index++)
        {
            TEnum value = Enum.Parse<TEnum>(fieldNames[index]);
            string name = ExternalName(fieldNames[index], namingPolicy);
            Arguments.WellFormed(name);

            if (!byName.TryAdd(name, value))
            {
                throw new ArgumentException($"Two members of {typeof(TEnum).Name} are written as \"{name}\".");
            }

            // Members that share a value are all accepted. The first one in value order is the name written back.
            byValue.TryAdd(value, name);
            elements.Add(JsonStrings.ToElement(name));
        }

        SchemaNode node = new() { Types = JsonTypes.String, Enum = elements.MoveToImmutable() };
        return new EnumSchema<TEnum>(node, byName.ToFrozenDictionary(StringComparer.Ordinal), byValue.ToFrozenDictionary());
    }

    // Like JsonStringEnumConverter: an explicit name is used as written, and a naming policy changes only the others.
    private static string ExternalName(string fieldName, JsonNamingPolicy? namingPolicy)
    {
        if (typeof(TEnum).GetField(fieldName, BindingFlags.Public | BindingFlags.Static)?.GetCustomAttribute<JsonStringEnumMemberNameAttribute>() is { } attribute)
        {
            return attribute.Name;
        }

        return namingPolicy?.ConvertName(fieldName) ?? fieldName;
    }

    private protected override EnumSchema<TEnum> With(SchemaNode node) => new(node, _byName, _byValue);

    internal override TEnum Materialize(JsonElement value) =>
        _byName.TryGetValue(JsonText.GetString(value), out TEnum member) ? member : throw new JsonException("The value isn't a member name.");

    internal override JsonElement ToElement(TEnum value) =>
        _byValue.TryGetValue(value, out string? name)
            ? JsonStrings.ToElement(name)
            : throw new JsonException($"{value} isn't a member of {typeof(TEnum).Name}.");
}
