using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Zorg.Json.Validation;

internal static class JsonStrings
{
    public static JsonElement ToElement(string value)
    {
        ArrayBufferWriter<byte> buffer = new();

        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStringValue(value);
        }

        return JsonElement.Parse(buffer.WrittenSpan);
    }

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
}
