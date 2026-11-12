using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;

namespace Zorg.Json.Validation;

/// <summary>
/// Reads strings and property names from a <see cref="JsonElement"/> without throwing. JSON text may hold a lone
/// surrogate escape such as <c>"\ud800"</c>, which <see cref="JsonElement.GetString"/> refuses to decode, and a caller can
/// hand us an element parsed from bytes that aren't UTF-8. Both are read here as a .NET string that keeps every
/// distinct input distinct: a lone surrogate stays itself and an invalid byte becomes <c>U+DC80</c> to <c>U+DCFF</c>.
/// </summary>
internal static class JsonText
{
    public static string GetString(JsonElement element)
    {
        _ = TryGetString(element, out string text);
        return text;
    }

    public static string GetName(JsonProperty property)
    {
        _ = TryGetName(property, out string name);
        return name;
    }

    /// <summary>False when the text can't be decoded. <paramref name="text"/> is then the lossless reading described above.</summary>
    public static bool TryGetString(JsonElement element, out string text)
    {
        try
        {
            text = element.GetString()!;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            ReadOnlySpan<byte> raw = JsonMarshal.GetRawUtf8Value(element);
            text = Decode(raw.Length >= 2 ? raw[1..^1] : raw);
            return false;
        }
    }

    public static bool TryGetName(JsonProperty property, out string name)
    {
        try
        {
            name = property.Name;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            name = Decode(JsonMarshal.GetRawUtf8PropertyName(property));
            return false;
        }
    }

    /// <summary>The raw text of a string value or property name, without the quotes and with escapes as written.</summary>
    public static ReadOnlySpan<byte> RawString(JsonElement element)
    {
        ReadOnlySpan<byte> raw = JsonMarshal.GetRawUtf8Value(element);
        return raw.Length >= 2 ? raw[1..^1] : raw;
    }

    /// <summary>Looks a property up by name. The last one wins when a name is repeated.</summary>
    public static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        try
        {
            return element.TryGetProperty(name, out value);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            bool found = false;
            value = default;

            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(GetName(property), name, StringComparison.Ordinal))
                {
                    value = property.Value;
                    found = true;
                }
            }

            return found;
        }
    }

    /// <summary>The properties by name, for many lookups. Where a name is repeated, the last one wins.</summary>
    public static Dictionary<string, JsonElement> Index(JsonElement element)
    {
        Dictionary<string, JsonElement> index = new(StringComparer.Ordinal);

        foreach (JsonProperty property in element.EnumerateObject())
        {
            index[GetName(property)] = property.Value;
        }

        return index;
    }

    /// <summary>Writes a string as a JSON string literal, keeping a lone surrogate as an escape.</summary>
    public static void AppendLiteral(StringBuilder builder, string value)
    {
        builder.Append('"');

        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];

            if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                builder.Append(character).Append(value[++index]);
                continue;
            }

            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;

                case '\\':
                    builder.Append("\\\\");
                    break;

                case < ' ' or >= '\ud800' and <= '\udfff':
                    builder.Append("\\u").Append(((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    break;

                default:
                    builder.Append(character);
                    break;
            }
        }

        builder.Append('"');
    }

    /// <summary>True when a .NET string has a surrogate with no partner, so it can't be written as UTF-8 or UTF-16 JSON.</summary>
    public static bool HasLoneSurrogate(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                index++;
            }
            else if (char.IsSurrogate(value[index]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the JSON text holds a string a .NET string can't represent as UTF-8: bytes that aren't UTF-8, or a
    /// surrogate escape with no partner.
    /// </summary>
    public static bool HasUndecodableString(ReadOnlySpan<byte> utf8)
    {
        if (!Utf8.IsValid(utf8))
        {
            return true;
        }

        int index = utf8.IndexOf((byte)'\\');

        while (index >= 0 && index < utf8.Length)
        {
            ReadOnlySpan<byte> rest = utf8[(index + 1)..];

            if (rest.Length > 0 && rest[0] == (byte)'u')
            {
                if (!TryHex(rest, out int unit))
                {
                    return false;
                }

                if (unit is >= 0xD800 and <= 0xDBFF)
                {
                    if (rest.Length >= 11 && rest[5] == (byte)'\\' && rest[6] == (byte)'u' && TryHex(rest[6..], out int next) && next is >= 0xDC00 and <= 0xDFFF)
                    {
                        index += 12;
                    }
                    else
                    {
                        return true;
                    }
                }
                else if (unit is >= 0xDC00 and <= 0xDFFF)
                {
                    return true;
                }
                else
                {
                    index += 6;
                }
            }
            else
            {
                index += 2;
            }

            if (index >= utf8.Length)
            {
                break;
            }

            int skip = utf8[index..].IndexOf((byte)'\\');
            index = skip < 0 ? -1 : index + skip;
        }

        return false;
    }

    private static bool TryHex(ReadOnlySpan<byte> escape, out int value)
    {
        value = 0;

        if (escape.Length < 5)
        {
            return false;
        }

        for (int index = 1; index <= 4; index++)
        {
            int digit = escape[index] switch
            {
                >= (byte)'0' and <= (byte)'9' => escape[index] - '0',
                >= (byte)'a' and <= (byte)'f' => escape[index] - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => escape[index] - 'A' + 10,
                _ => -1,
            };

            if (digit < 0)
            {
                return false;
            }

            value = (value << 4) | digit;
        }

        return true;
    }

    private static string Decode(ReadOnlySpan<byte> raw)
    {
        StringBuilder builder = new(raw.Length);

        for (int index = 0; index < raw.Length;)
        {
            byte current = raw[index];

            if (current == (byte)'\\' && index + 1 < raw.Length)
            {
                byte escape = raw[index + 1];
                index += 2;

                switch (escape)
                {
                    case (byte)'b':
                        builder.Append('\b');
                        break;

                    case (byte)'f':
                        builder.Append('\f');
                        break;

                    case (byte)'n':
                        builder.Append('\n');
                        break;

                    case (byte)'r':
                        builder.Append('\r');
                        break;

                    case (byte)'t':
                        builder.Append('\t');
                        break;

                    case (byte)'u':
                        if (TryHex(raw[(index - 1)..], out int unit))
                        {
                            builder.Append((char)unit);
                            index += 4;
                        }

                        break;

                    default:
                        builder.Append((char)escape);
                        break;
                }

                continue;
            }

            if (current < 0x80)
            {
                builder.Append((char)current);
                index++;
                continue;
            }

            if (Rune.DecodeFromUtf8(raw[index..], out Rune rune, out int consumed) == OperationStatus.Done)
            {
#if NET11_0_OR_GREATER
                builder.Append(rune);
#else
                builder.Append(rune.ToString());
#endif
                index += consumed;
            }
            else
            {
                builder.Append((char)(0xDC00 + current));
                index++;
            }
        }

        return builder.ToString();
    }
}
