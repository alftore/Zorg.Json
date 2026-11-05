using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Zorg.Json;

/// <summary>
/// Reads and writes <see cref="Optional{T}"/>. Put it on a property with <c>[JsonConverter(typeof(OptionalJsonConverter&lt;string&gt;))]</c>.
/// It needs no reflection, so it works with source generation and Native AOT.
/// </summary>
/// <typeparam name="T">The type of the value.</typeparam>
/// <remarks>
/// A property that's missing from the JSON never reaches the converter and stays unset. JSON <c>null</c> reads as a set
/// <see langword="null"/> when <typeparamref name="T"/> allows it, and fails with a <see cref="JsonException"/> otherwise.
/// An unset value is written as <c>null</c> because a converter can't leave out the property; see <see cref="Optional{T}"/>
/// and <see cref="OptionalJson.OmitUnset"/> for how to omit it. The converter finds how to read <typeparamref name="T"/>
/// through the options, so <typeparamref name="T"/> needs type info in a source generation context unless it's a built-in type.
/// </remarks>
/// <example>
/// <code>
/// [JsonConverter(typeof(OptionalJsonConverter&lt;int&gt;))]
/// [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
/// public Optional&lt;int&gt; Age { get; init; }
/// </code>
/// </example>
public sealed class OptionalJsonConverter<T> : JsonConverter<Optional<T>>
{
    /// <inheritdoc/>
    public override bool HandleNull => true;

    /// <inheritdoc/>
    public override Optional<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (reader.TokenType == JsonTokenType.Null)
        {
            return default(T) is null
                ? new Optional<T>(default)
                : throw new JsonException($"null is not a valid {typeof(T).Name}.");
        }

        return new Optional<T>(JsonSerializer.Deserialize(ref reader, Inner(options)));
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, Optional<T> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(options);

        if (value.IsSet && value.Value is { } inner)
        {
            JsonSerializer.Serialize(writer, inner, Inner(options));
        }
        else
        {
            writer.WriteNullValue();
        }
    }

    private static JsonTypeInfo<T> Inner(JsonSerializerOptions options) =>
        (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
}

/// <summary>
/// Creates an <see cref="OptionalJsonConverter{T}"/> for any <see cref="Optional{T}"/>, for code that registers
/// converters on <see cref="JsonSerializerOptions"/> instead of annotating each property.
/// </summary>
/// <remarks>
/// It builds the converter through reflection, so it isn't trim or AOT safe. With source generation, annotate each property
/// with <see cref="OptionalJsonConverter{T}"/> instead.
/// </remarks>
/// <example>
/// <code>
/// JsonSerializerOptions options = new() { Converters = { new OptionalJsonConverterFactory() } };
/// </code>
/// </example>
[RequiresUnreferencedCode("Creates the converter with reflection. Annotate properties with OptionalJsonConverter<T> for trimming.")]
[RequiresDynamicCode("Creates the converter with reflection. Annotate properties with OptionalJsonConverter<T> for Native AOT.")]
public sealed class OptionalJsonConverterFactory : JsonConverterFactory
{
    /// <inheritdoc/>
    public override bool CanConvert(Type typeToConvert)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);
        return typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Optional<>);
    }

    /// <inheritdoc/>
    [UnconditionalSuppressMessage("Trimming", "IL2055", Justification = "The factory itself is marked as requiring unreferenced code.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The factory itself is marked as requiring dynamic code.")]
    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);
        Type converter = typeof(OptionalJsonConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]);
        return (JsonConverter?)Activator.CreateInstance(converter, BindingFlags.Public | BindingFlags.Instance, null, null, null);
    }
}

/// <summary>Helpers for serializing <see cref="Optional{T}"/>.</summary>
public static class OptionalJson
{
    /// <summary>
    /// A type info modifier that leaves out every <see cref="Optional{T}"/> property that isn't set, so no attribute is
    /// needed on each one. It needs no reflection of its own.
    /// </summary>
    /// <param name="typeInfo">The type info being built.</param>
    /// <example>
    /// <code>
    /// JsonSerializerOptions options = new()
    /// {
    ///     TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { OptionalJson.OmitUnset } },
    /// };
    /// </code>
    /// </example>
    public static void OmitUnset(JsonTypeInfo typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);

        if (typeInfo.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        foreach (JsonPropertyInfo property in typeInfo.Properties)
        {
            if (property.PropertyType.IsGenericType && property.PropertyType.GetGenericTypeDefinition() == typeof(Optional<>))
            {
                property.ShouldSerialize = static (_, value) => value is IOptional { IsSet: true };
            }
        }
    }
}
