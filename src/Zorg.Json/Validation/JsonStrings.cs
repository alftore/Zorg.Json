using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Zorg.Json.Validation;

internal static class JsonStrings
{
    public static JsonElement ToElement(string value) => Write(writer => writer.WriteStringValue(value));

    public static JsonElement ToArray(IEnumerable<JsonElement> items) => Write(writer =>
    {
        writer.WriteStartArray();

        foreach (JsonElement item in items)
        {
            item.WriteTo(writer);
        }

        writer.WriteEndArray();
    });

    public static JsonElement ToObject(IEnumerable<KeyValuePair<string, JsonElement>> properties) => Write(writer =>
    {
        writer.WriteStartObject();

        foreach ((string name, JsonElement value) in properties)
        {
            writer.WritePropertyName(name);
            value.WriteTo(writer);
        }

        writer.WriteEndObject();
    });

    /// <summary>JSON Schema measures strings in Unicode code points, not UTF-16 units.</summary>
    public static int CodePointCount(string value)
    {
        int count = 0;

        foreach (Rune _ in value.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    private static JsonElement Write(Action<Utf8JsonWriter> write)
    {
        ArrayBufferWriter<byte> buffer = new();

        using (Utf8JsonWriter writer = new(buffer))
        {
            write(writer);
        }

        return JsonElement.Parse(buffer.WrittenSpan);
    }
}
