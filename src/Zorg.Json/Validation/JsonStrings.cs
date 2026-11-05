using System.Collections.Generic;
using System.Text;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Zorg.Json.Validation;

internal static class JsonStrings
{
    private static readonly JsonDocumentOptions ReaderOptions = new() { MaxDepth = int.MaxValue };

    /// <summary>Keeps a lone surrogate as an escape, which <see cref="Utf8JsonWriter"/> would refuse or replace.</summary>
    public static JsonElement ToElement(string? value)
    {
        if (value is null)
        {
            return JsonElement.Parse("null");
        }

        StringBuilder text = new(value.Length + 2);
        JsonText.AppendLiteral(text, value);
        return JsonElement.Parse(text.ToString(), ReaderOptions);
    }

    /// <summary>
    /// Copies the raw UTF-8, because <see cref="JsonElement.WriteTo"/> throws on a lone surrogate and
    /// <see cref="JsonElement.GetRawText"/> throws on bytes that aren't UTF-8.
    /// </summary>
    public static JsonElement ToArray(IEnumerable<JsonElement> items)
    {
        List<byte> bytes = [(byte)'['];
        bool first = true;

        foreach (JsonElement item in items)
        {
            if (!first)
            {
                bytes.Add((byte)',');
            }

            bytes.AddRange(JsonMarshal.GetRawUtf8Value(item));
            first = false;
        }

        bytes.Add((byte)']');
        return Parse(bytes);
    }

    /// <summary>Copies the raw UTF-8, like <see cref="ToArray"/>.</summary>
    public static JsonElement ToObject(IEnumerable<KeyValuePair<string, JsonElement>> properties)
    {
        List<byte> bytes = [(byte)'{'];
        StringBuilder name = new();
        bool first = true;

        foreach ((string key, JsonElement value) in properties)
        {
            if (!first)
            {
                bytes.Add((byte)',');
            }

            name.Clear();
            JsonText.AppendLiteral(name, key);
            bytes.AddRange(Encoding.UTF8.GetBytes(name.ToString()));
            bytes.Add((byte)':');
            bytes.AddRange(JsonMarshal.GetRawUtf8Value(value));
            first = false;
        }

        bytes.Add((byte)'}');
        return Parse(bytes);
    }

    private static JsonElement Parse(List<byte> bytes) =>
        JsonElement.Parse(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes), ReaderOptions);

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
