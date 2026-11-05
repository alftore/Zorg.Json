using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Zorg.Json.Validation;

internal static class JsonStrings
{
    private static readonly JsonDocumentOptions ReaderOptions = new() { MaxDepth = int.MaxValue };

    /// <summary>Keeps a lone surrogate as an escape, which <see cref="Utf8JsonWriter"/> would refuse or replace.</summary>
    public static JsonElement ToElement(string value)
    {
        StringBuilder text = new(value.Length + 2);
        JsonText.AppendLiteral(text, value);
        return JsonElement.Parse(text.ToString(), ReaderOptions);
    }

    /// <summary>Copies raw text, because <see cref="JsonElement.WriteTo"/> throws on a lone surrogate.</summary>
    public static JsonElement ToArray(IEnumerable<JsonElement> items)
    {
        StringBuilder text = new("[");
        bool first = true;

        foreach (JsonElement item in items)
        {
            text.Append(first ? "" : ",").Append(item.GetRawText());
            first = false;
        }

        return JsonElement.Parse(text.Append(']').ToString(), ReaderOptions);
    }

    /// <summary>Copies raw text, because <see cref="JsonElement.WriteTo"/> throws on a lone surrogate.</summary>
    public static JsonElement ToObject(IEnumerable<KeyValuePair<string, JsonElement>> properties)
    {
        StringBuilder text = new("{");
        bool first = true;

        foreach ((string name, JsonElement value) in properties)
        {
            text.Append(first ? "" : ",");
            JsonText.AppendLiteral(text, name);
            text.Append(':').Append(value.GetRawText());
            first = false;
        }

        return JsonElement.Parse(text.Append('}').ToString(), ReaderOptions);
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
