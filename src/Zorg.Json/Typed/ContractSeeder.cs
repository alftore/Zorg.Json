using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Zorg.Json.Model;

namespace Zorg.Json.Typed;

/// <summary>
/// Builds a <see cref="SchemaNode"/> from a System.Text.Json contract, so that a typed schema covers every
/// property even when the caller configures none of them.
/// </summary>
internal sealed class ContractSeeder
{
    private static readonly SchemaNode AnyValue = new();

    private readonly JsonSerializerOptions _options;
    private readonly Dictionary<Type, SchemaNode> _finished = [];
    private readonly HashSet<Type> _inProgress = [];

    private ContractSeeder(JsonSerializerOptions options) => _options = options;

    public static SchemaNode Seed(JsonTypeInfo typeInfo)
    {
        ContractSeeder seeder = new(typeInfo.Options);
        return typeInfo.Kind == JsonTypeInfoKind.Object ? seeder.SeedObject(typeInfo) : seeder.Seed(typeInfo.Type);
    }

    /// <summary>A constructor parameter without a default is required, although System.Text.Json doesn't enforce it.</summary>
    public static bool IsRequired(JsonPropertyInfo property) =>
        property.IsRequired
        || property.AssociatedParameter is { HasDefaultValue: false, IsMemberInitializer: false };

    public static bool IsReadable(JsonPropertyInfo property) => property.Set is not null || property.AssociatedParameter is not null;

    private static SchemaNode Scalar(JsonTypes types, string? format = null) =>
        new() { Types = types, Format = format, Behavior = new SchemaBehavior { FormatIsFluent = format is not null } };

    private static SchemaNode? Primitive(Type type) => type switch
    {
        _ when type == typeof(string) => Scalar(JsonTypes.String),
        _ when type == typeof(char) => new SchemaNode { Types = JsonTypes.String, MinLength = 1, MaxLength = 1 },
        _ when type == typeof(bool) => Scalar(JsonTypes.Boolean),
        _ when type == typeof(byte) => NumberNode<byte>(),
        _ when type == typeof(sbyte) => NumberNode<sbyte>(),
        _ when type == typeof(short) => NumberNode<short>(),
        _ when type == typeof(ushort) => NumberNode<ushort>(),
        _ when type == typeof(int) => NumberNode<int>(),
        _ when type == typeof(uint) => NumberNode<uint>(),
        _ when type == typeof(long) => NumberNode<long>(),
        _ when type == typeof(ulong) => NumberNode<ulong>(),
        _ when type == typeof(float) => NumberNode<float>(),
        _ when type == typeof(double) => NumberNode<double>(),
        _ when type == typeof(decimal) => NumberNode<decimal>(),
        _ when type == typeof(Guid) => Scalar(JsonTypes.String, "uuid"),
        _ when type == typeof(DateTimeOffset) => Scalar(JsonTypes.String, "date-time"),
        _ when type == typeof(DateTime) => Scalar(JsonTypes.String),
        _ when type == typeof(DateOnly) => Scalar(JsonTypes.String, "date"),
        _ when type == typeof(TimeOnly) => Scalar(JsonTypes.String),
        _ when type == typeof(TimeSpan) => Scalar(JsonTypes.String),
        _ when type == typeof(Uri) => Scalar(JsonTypes.String, "uri-reference"),
        _ when type == typeof(byte[]) => new SchemaNode { Types = JsonTypes.String, ContentEncoding = "base64" },
        _ when type == typeof(JsonElement) || type == typeof(object) => new SchemaNode(),
        _ => null,
    };

    private static SchemaNode NumberNode<TNumber>()
        where TNumber : struct, System.Numerics.INumber<TNumber> => NumberSchema<TNumber>.CreateNode(strict: true);

    private SchemaNode Seed(Type type)
    {
        Type target = Nullable.GetUnderlyingType(type) ?? type;

        // A custom converter decides the JSON shape, and it can't be known from here.
        if (_options.TryGetTypeInfo(target, out JsonTypeInfo? known) && known.Converter.GetType().Assembly != typeof(JsonSerializer).Assembly)
        {
            return new SchemaNode();
        }

        if (Primitive(target) is { } primitive)
        {
            return primitive;
        }

        JsonTypeInfo typeInfo = _options.GetTypeInfo(target);

        if (target.IsEnum)
        {
            return EnumNode(target, typeInfo);
        }

        return typeInfo.Kind switch
        {
            JsonTypeInfoKind.Enumerable => new SchemaNode { Types = JsonTypes.Array, Items = SeedElement(typeInfo.ElementType!) },
            JsonTypeInfoKind.Dictionary => new SchemaNode { Types = JsonTypes.Object, AdditionalProperties = SeedElement(typeInfo.ElementType!) },
            JsonTypeInfoKind.Object => Reference(typeInfo),
            _ => new SchemaNode(),
        };
    }

    private SchemaNode SeedElement(Type type)
    {
        SchemaNode node = Seed(type);
        return Nullable.GetUnderlyingType(type) is null ? node : node.WithNull();
    }

    private SchemaNode Reference(JsonTypeInfo typeInfo)
    {
        Type type = typeInfo.Type;

        if (_finished.TryGetValue(type, out SchemaNode? finished))
        {
            return finished;
        }

        if (_inProgress.Contains(type))
        {
            return new SchemaNode
            {
                Ref = $"#/$defs/{type.Name}",
                Behavior = new SchemaBehavior { DefinitionName = type.Name, Target = () => _finished[type] },
            };
        }

        return SeedObject(typeInfo);
    }

    private SchemaNode SeedObject(JsonTypeInfo typeInfo)
    {
        Type type = typeInfo.Type;
        _inProgress.Add(type);

        SchemaNode node = typeInfo.PolymorphismOptions is { DerivedTypes.Count: > 0 } polymorphism
            ? Polymorphic(polymorphism)
            : Properties(typeInfo);

        _inProgress.Remove(type);
        _finished[type] = node;
        return node;
    }

    private SchemaNode Properties(JsonTypeInfo typeInfo)
    {
        ImmutableArray<SchemaProperty>.Builder properties = ImmutableArray.CreateBuilder<SchemaProperty>();
        ImmutableArray<string>.Builder required = ImmutableArray.CreateBuilder<string>();
        bool hasExtensionData = false;

        foreach (JsonPropertyInfo property in typeInfo.Properties)
        {
            if (property.IsExtensionData)
            {
                hasExtensionData = true;
                continue;
            }

            bool readable = IsReadable(property);

            if (!readable && property.Get is null)
            {
                continue;
            }

            Type? optional = OptionalValueType(property.PropertyType);
            SchemaNode schema;

            if (optional is not null)
            {
                schema = SeedOptional(property, optional);
            }
            else
            {
                schema = property.CustomConverter is not null ? new SchemaNode() : Seed(property.PropertyType);

                if (schema != AnyValue && (property.IsSetNullable || Nullable.GetUnderlyingType(property.PropertyType) is not null))
                {
                    schema = schema.WithNull();
                }
            }

            JsonElement? defaultValue = optional is null ? DefaultValue(property) : null;

            if (defaultValue is not null)
            {
                schema = schema with { Default = defaultValue };
            }

            properties.Add(new SchemaProperty(property.Name, schema) { OutputOnly = !readable });

            if (readable && (property.IsRequired || (optional is null && defaultValue is null && IsRequired(property))))
            {
                required.Add(property.Name);
            }
        }

        return new SchemaNode
        {
            Types = JsonTypes.Object,
            Properties = properties.ToImmutable(),
            Required = required.ToImmutable(),
            Behavior = new SchemaBehavior
            {
                UnknownProperties = hasExtensionData ? UnknownProperties.Allow : UnknownProperties.Strip,
                IgnoresNameCase = _options.PropertyNameCaseInsensitive,
            },
        };
    }

    /// <summary>The <c>T</c> of an <see cref="Optional{T}"/>, or null for any other type. Reads the type arguments only, so it's safe for Native AOT.</summary>
    private static Type? OptionalValueType(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Optional<>) ? type.GetGenericArguments()[0] : null;

    /// <summary>An optional property has the schema of its value, and JSON null is allowed when the value type is nullable.</summary>
    private SchemaNode SeedOptional(JsonPropertyInfo property, Type valueType)
    {
        SchemaNode schema = Seed(valueType);

        if (schema == AnyValue || valueType.IsValueType && Nullable.GetUnderlyingType(valueType) is null)
        {
            return schema;
        }

        return AllowsNull(property, valueType) ? schema.WithNull() : schema;
    }

    private static bool AllowsNull(JsonPropertyInfo property, Type valueType)
    {
        if (valueType.IsValueType)
        {
            return true;
        }

        // Without nullable annotations, such as a type from an older assembly, null is allowed: the converter accepts it.
        if (property.AttributeProvider is not PropertyInfo member)
        {
            return true;
        }

        NullabilityInfo info = new NullabilityInfoContext().Create(member);
        return info.GenericTypeArguments.Length == 0 || info.GenericTypeArguments[0].ReadState != NullabilityState.NotNull;
    }

    /// <summary>Reads <see cref="DefaultValueAttribute"/>. Like the attribute, it's an annotation: nothing fills the value in.</summary>
    private JsonElement? DefaultValue(JsonPropertyInfo property)
    {
        if (property.AttributeProvider?.GetCustomAttributes(typeof(DefaultValueAttribute), inherit: true) is not [DefaultValueAttribute attribute, ..]
            || !_options.TryGetTypeInfo(property.PropertyType, out JsonTypeInfo? typeInfo))
        {
            return null;
        }

        Type target = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        try
        {
            object? value = attribute.Value is null || target.IsInstanceOfType(attribute.Value)
                ? attribute.Value
                : Convert.ChangeType(attribute.Value, target, CultureInfo.InvariantCulture);

            return JsonSerializer.SerializeToElement(value, typeInfo);
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException or NotSupportedException)
        {
            return null;
        }
    }

    private SchemaNode Polymorphic(JsonPolymorphismOptions polymorphism)
    {
        string discriminator = polymorphism.TypeDiscriminatorPropertyName;
        ImmutableArray<SchemaNode>.Builder branches = ImmutableArray.CreateBuilder<SchemaNode>();

        foreach (JsonDerivedType derived in polymorphism.DerivedTypes)
        {
            SchemaNode branch = Properties(_options.GetTypeInfo(derived.DerivedType));
            JsonElement constant = derived.TypeDiscriminator switch
            {
                string text => Validation.JsonStrings.ToElement(text),
                int number => JsonElement.Parse(number.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                _ => throw new NotSupportedException($"{derived.DerivedType.Name} has no type discriminator, so its JSON can't be told apart."),
            };

            branches.Add(branch with
            {
                Properties = branch.Properties.Insert(0, new SchemaProperty(discriminator, new SchemaNode { Const = constant })),
                Required = branch.Required.Insert(0, discriminator),
            });
        }

        return new SchemaNode
        {
            OneOf = branches.ToImmutable(),
            Behavior = new SchemaBehavior { Discriminator = discriminator },
        };
    }

    private static SchemaNode EnumNode(Type type, JsonTypeInfo typeInfo)
    {
        if (type.IsDefined(typeof(FlagsAttribute), inherit: false))
        {
            return new SchemaNode { Types = JsonTypes.String | JsonTypes.Integer };
        }

        // Serializing each member is the only way to see what the converter and naming policy produce.
        ImmutableArray<JsonElement> values = [.. Enum.GetValuesAsUnderlyingType(type).Cast<object>()
            .Select(value => JsonSerializer.SerializeToElement(Enum.ToObject(type, value), typeInfo))];

        return new SchemaNode { Enum = values };
    }
}
