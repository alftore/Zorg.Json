using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Zorg.Json.Validation;

/// <summary>The checks behind the <c>format</c> keyword. Every check is written from its RFC grammar, so that it doesn't depend on the platform.</summary>
internal static partial class StringFormats
{
    private const string UriSubDelimiters = "!$&'()*+,;=";
    private const string EmailSymbols = "!#$%&'*+-/=?^_`{|}~";
    private const string UriTemplateOperators = "+#./;?&=,!@|";

    /// <summary>Gets the check for a format. Null means the format is only an annotation.</summary>
    public static Func<string, bool>? Find(string? format) => format switch
    {
        "date" => IsDate,
        "date-time" => IsDateTime,
        "duration" => IsDuration,
        "email" => value => IsEmail(value, unicode: false),
        "hostname" => Idna.IsHostname,
        "idn-email" => value => IsEmail(value, unicode: true),
        "idn-hostname" => Idna.IsIdnHostname,
        "ipv4" => IsIpv4,
        "ipv6" => IsIpv6,
        "iri" => value => IsUri(value, relative: false, unicode: true),
        "iri-reference" => value => IsUri(value, relative: true, unicode: true),
        "json-pointer" => IsJsonPointer,
        "regex" => IsRegex,
        "relative-json-pointer" => IsRelativeJsonPointer,
        "time" => IsTime,
        "uri" => value => IsUri(value, relative: false, unicode: false),
        "uri-reference" => value => IsUri(value, relative: true, unicode: false),
        "uri-template" => IsUriTemplate,
        "uuid" => IsUuid,
        _ => null,
    };

    public static bool IsKnown(string format) => Find(format) is not null;

    // Guid parsing tolerates surrounding whitespace, which the format doesn't.
    private static bool IsUuid(string value) => value.Length == 36 && Guid.TryParseExact(value, "D", out _);

    private static bool IsRegex(string value)
    {
        try
        {
            PatternCompiler.Compile(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsDate(string value) => IsDate(value.AsSpan());

    private static bool IsDate(ReadOnlySpan<char> value)
    {
        if (value.Length != 10 || value[4] != '-' || value[7] != '-'
            || !TryDigits(value[..4], out int year) || !TryDigits(value[5..7], out int month) || !TryDigits(value[8..], out int day))
        {
            return false;
        }

        if (month is < 1 or > 12 || day < 1)
        {
            return false;
        }

        bool isLeap = year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);

        int days = month switch
        {
            2 => isLeap ? 29 : 28,
            4 or 6 or 9 or 11 => 30,
            _ => 31,
        };

        return day <= days;
    }

    private static bool IsDateTime(string value) =>
        value.Length > 11 && value[10] is 'T' or 't' && IsDate(value.AsSpan(0, 10)) && IsTime(value.AsSpan(11));

    private static bool IsTime(string value) => IsTime(value.AsSpan());

    private static bool IsTime(ReadOnlySpan<char> value)
    {
        if (value.Length < 9 || value[2] != ':' || value[5] != ':'
            || !TryDigits(value[..2], out int hour) || !TryDigits(value[3..5], out int minute) || !TryDigits(value[6..8], out int second))
        {
            return false;
        }

        ReadOnlySpan<char> rest = value[8..];

        if (rest[0] == '.')
        {
            int digits = 1;

            while (digits < rest.Length && char.IsAsciiDigit(rest[digits]))
            {
                digits++;
            }

            if (digits == 1)
            {
                return false;
            }

            rest = rest[digits..];
        }

        if (!TryOffset(rest, out int offset) || hour > 23 || minute > 59)
        {
            return false;
        }

        // A leap second happens at 23:59:60 UTC, whatever the offset.
        int utc = (((hour * 60) + minute - offset) % 1440 + 1440) % 1440;
        return second < 60 || (second == 60 && utc == (23 * 60) + 59);
    }

    private static bool TryOffset(ReadOnlySpan<char> value, out int minutes)
    {
        minutes = 0;

        if (value is "Z" or "z")
        {
            return true;
        }

        if (value.Length != 6 || value[0] is not ('+' or '-') || value[3] != ':'
            || !TryDigits(value[1..3], out int hour) || !TryDigits(value[4..], out int minute) || hour > 23 || minute > 59)
        {
            return false;
        }

        minutes = ((hour * 60) + minute) * (value[0] == '-' ? -1 : 1);
        return true;
    }

    private static bool TryDigits(ReadOnlySpan<char> value, out int number)
    {
        number = 0;

        foreach (char character in value)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }

            number = (number * 10) + (character - '0');
        }

        return true;
    }

    private static bool IsDuration(string value) => DurationShape().IsMatch(value);

    private static bool IsIpv4(string value) => IsIpv4(value.AsSpan());

    private static bool IsIpv4(ReadOnlySpan<char> value)
    {
        int parts = 0;

        foreach (Range range in value.Split('.'))
        {
            ReadOnlySpan<char> part = value[range];

            if (++parts > 4 || part.Length is 0 or > 3 || (part.Length > 1 && part[0] == '0') || !TryDigits(part, out int octet) || octet > 255)
            {
                return false;
            }
        }

        return parts == 4;
    }

    private static bool IsIpv6(string value) => IsIpv6(value.AsSpan());

    private static bool IsIpv6(ReadOnlySpan<char> value)
    {
        int compression = value.IndexOf("::", StringComparison.Ordinal);

        if (compression < 0)
        {
            return TryCountIpv6Groups(value, allowIpv4: true, out int groups) && groups == 8;
        }

        ReadOnlySpan<char> head = value[..compression];
        ReadOnlySpan<char> tail = value[(compression + 2)..];

        return !tail.Contains("::", StringComparison.Ordinal)
            && TryCountIpv6Groups(head, allowIpv4: false, out int before)
            && TryCountIpv6Groups(tail, allowIpv4: true, out int after)
            && before + after <= 7;
    }

    private static bool TryCountIpv6Groups(ReadOnlySpan<char> value, bool allowIpv4, out int count)
    {
        count = 0;

        if (value.IsEmpty)
        {
            return true;
        }

        foreach (Range range in value.Split(':'))
        {
            ReadOnlySpan<char> group = value[range];

            if (group.Contains('.'))
            {
                count += 2;

                if (!allowIpv4 || range.End.GetOffset(value.Length) != value.Length || !IsIpv4(group))
                {
                    return false;
                }

                continue;
            }

            count++;

            if (group.Length is 0 or > 4 || group.ContainsAnyExcept(HexDigits()))
            {
                return false;
            }
        }

        return true;
    }

    private static ReadOnlySpan<char> HexDigits() => "0123456789abcdefABCDEF";

    private static bool IsEmail(string value, bool unicode)
    {
        int index = ReadLocalPart(value, unicode);

        if (index <= 0 || index >= value.Length || value[index] != '@' || Encoding.UTF8.GetByteCount(value.AsSpan(0, index)) > 64)
        {
            return false;
        }

        ReadOnlySpan<char> domain = value.AsSpan(index + 1);

        if (domain.StartsWith("["))
        {
            return domain.EndsWith("]") && IsAddressLiteral(domain[1..^1]);
        }

        string host = domain.ToString();
        return unicode ? Idna.IsIdnHostname(host) : Idna.IsHostname(host);
    }

    private static bool IsAddressLiteral(ReadOnlySpan<char> value) =>
        value.StartsWith("IPv6:", StringComparison.OrdinalIgnoreCase) ? IsIpv6(value[5..]) : IsIpv4(value);

    /// <summary>Returns the index just past the local part, or -1 if it's malformed.</summary>
    private static int ReadLocalPart(string value, bool unicode)
    {
        if (value.StartsWith('"'))
        {
            return ReadQuotedString(value, unicode);
        }

        int index = 0;
        bool atStart = true;

        for (; index < value.Length && value[index] != '@'; index++)
        {
            char character = value[index];

            if (character == '.')
            {
                if (atStart)
                {
                    return -1;
                }

                atStart = true;
            }
            else if (char.IsAsciiLetterOrDigit(character) || EmailSymbols.Contains(character, StringComparison.Ordinal) || (unicode && character >= 0x80))
            {
                atStart = false;
            }
            else
            {
                return -1;
            }
        }

        return atStart ? -1 : index;
    }

    private static int ReadQuotedString(string value, bool unicode)
    {
        for (int index = 1; index < value.Length; index++)
        {
            char character = value[index];

            if (character == '"')
            {
                return index + 1;
            }

            if (character == '\\')
            {
                index++;

                if (index == value.Length || !IsQuotable(value[index], unicode))
                {
                    return -1;
                }
            }
            else if (!IsQuotable(character, unicode))
            {
                return -1;
            }
        }

        return -1;
    }

    private static bool IsQuotable(char character, bool unicode) => character is >= ' ' and <= '~' || (unicode && character >= 0x80);

    private static bool IsJsonPointer(string value) => IsJsonPointer(value.AsSpan());

    private static bool IsJsonPointer(ReadOnlySpan<char> value)
    {
        if (!value.IsEmpty && value[0] != '/')
        {
            return false;
        }

        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] == '~' && (index + 1 == value.Length || value[index + 1] is not ('0' or '1')))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsRelativeJsonPointer(string value)
    {
        int digits = 0;

        while (digits < value.Length && char.IsAsciiDigit(value[digits]))
        {
            digits++;
        }

        if (digits == 0 || (digits > 1 && value[0] == '0'))
        {
            return false;
        }

        ReadOnlySpan<char> rest = value.AsSpan(digits);
        return rest is "#" || IsJsonPointer(rest);
    }

    private static bool IsUri(string value, bool relative, bool unicode)
    {
        ReadOnlySpan<char> rest = value;
        int hash = rest.IndexOf('#');

        if (hash >= 0)
        {
            if (!IsUriText(rest[(hash + 1)..], "/?", unicode, allowPrivate: false))
            {
                return false;
            }

            rest = rest[..hash];
        }

        int question = rest.IndexOf('?');

        if (question >= 0)
        {
            if (!IsUriText(rest[(question + 1)..], "/?", unicode, allowPrivate: true))
            {
                return false;
            }

            rest = rest[..question];
        }

        int delimiter = rest.IndexOfAny(':', '/');

        if (delimiter >= 0 && rest[delimiter] == ':')
        {
            if (!IsScheme(rest[..delimiter]))
            {
                return false;
            }

            rest = rest[(delimiter + 1)..];
        }
        else if (!relative)
        {
            return false;
        }

        if (rest.StartsWith("//"))
        {
            rest = rest[2..];
            int slash = rest.IndexOf('/');
            ReadOnlySpan<char> authority = slash < 0 ? rest : rest[..slash];

            if (!IsAuthority(authority, unicode))
            {
                return false;
            }

            rest = slash < 0 ? default : rest[slash..];
        }

        return IsUriText(rest, "/", unicode, allowPrivate: false);
    }

    private static bool IsScheme(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || !char.IsAsciiLetter(value[0]))
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('+' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAuthority(ReadOnlySpan<char> value, bool unicode)
    {
        ReadOnlySpan<char> rest = value;
        int at = rest.IndexOf('@');

        if (at >= 0)
        {
            if (!IsUriText(rest[..at], string.Empty, unicode, allowPrivate: false, allowColon: true, allowAt: false))
            {
                return false;
            }

            rest = rest[(at + 1)..];
        }

        if (rest.StartsWith("["))
        {
            int close = rest.IndexOf(']');

            if (close < 0 || !IsIpLiteral(rest[1..close]))
            {
                return false;
            }

            rest = rest[(close + 1)..];
            return rest.IsEmpty || (rest[0] == ':' && TryDigits(rest[1..], out _));
        }

        int colon = rest.IndexOf(':');

        if (colon >= 0)
        {
            if (!TryDigits(rest[(colon + 1)..], out _))
            {
                return false;
            }

            rest = rest[..colon];
        }

        return IsUriText(rest, string.Empty, unicode, allowPrivate: false, allowColon: false, allowAt: false);
    }

    private static bool IsIpLiteral(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || value[0] is not ('v' or 'V'))
        {
            return IsIpv6(value);
        }

        int dot = value.IndexOf('.');

        return dot > 1
            && dot < value.Length - 1
            && !value[1..dot].ContainsAnyExcept(HexDigits())
            && IsUriText(value[(dot + 1)..], string.Empty, unicode: false, allowPrivate: false, allowColon: true, allowAt: false);
    }

    /// <summary>Checks unreserved characters, sub-delimiters, percent escapes and the given extras, which is <c>pchar</c> with the defaults.</summary>
    private static bool IsUriText(ReadOnlySpan<char> value, string extra, bool unicode, bool allowPrivate, bool allowColon = true, bool allowAt = true)
    {
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];

            if (character == '%')
            {
                if (!IsPercentEscape(value[index..]))
                {
                    return false;
                }

                index += 2;
            }
            else if (character < 0x80)
            {
                bool allowed = char.IsAsciiLetterOrDigit(character)
                    || character is '-' or '.' or '_' or '~'
                    || UriSubDelimiters.Contains(character, StringComparison.Ordinal)
                    || extra.Contains(character, StringComparison.Ordinal)
                    || (allowColon && character == ':')
                    || (allowAt && character == '@');

                if (!allowed)
                {
                    return false;
                }
            }
            else if (!unicode || !TryReadWideCharacter(value, ref index, allowPrivate))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPercentEscape(ReadOnlySpan<char> value) =>
        value.Length >= 3 && char.IsAsciiHexDigit(value[1]) && char.IsAsciiHexDigit(value[2]);

    private static bool TryReadWideCharacter(ReadOnlySpan<char> value, ref int index, bool allowPrivate)
    {
        if (Rune.DecodeFromUtf16(value[index..], out Rune rune, out int consumed) != System.Buffers.OperationStatus.Done)
        {
            return false;
        }

        index += consumed - 1;
        return IsUcsChar(rune.Value) || (allowPrivate && IsPrivateUse(rune.Value));
    }

    private static bool IsUcsChar(int codePoint) =>
        codePoint is (>= 0xA0 and <= 0xD7FF) or (>= 0xF900 and <= 0xFDCF) or (>= 0xFDF0 and <= 0xFFEF)
        || (codePoint is >= 0x10000 and <= 0xEFFFD && (codePoint & 0xFFFE) != 0xFFFE);

    private static bool IsPrivateUse(int codePoint) =>
        codePoint is (>= 0xE000 and <= 0xF8FF) or (>= 0xF0000 and <= 0xFFFFD) or (>= 0x100000 and <= 0x10FFFD);

    private static bool IsUriTemplate(string value)
    {
        ReadOnlySpan<char> rest = value;

        while (!rest.IsEmpty)
        {
            int index = 0;

            if (rest[0] == '{')
            {
                int close = rest.IndexOf('}');

                if (close < 0 || !IsTemplateExpression(rest[1..close]))
                {
                    return false;
                }

                rest = rest[(close + 1)..];
                continue;
            }

            if (!IsTemplateLiteral(rest, ref index))
            {
                return false;
            }

            rest = rest[(index + 1)..];
        }

        return true;
    }

    private static bool IsTemplateLiteral(ReadOnlySpan<char> value, ref int index)
    {
        char character = value[index];

        if (character == '%')
        {
            index += 2;
            return IsPercentEscape(value);
        }

        if (character >= 0x80)
        {
            return TryReadWideCharacter(value, ref index, allowPrivate: true);
        }

        return character > ' ' && character != 0x7F && !"\"<>\\^`{|}".Contains(character, StringComparison.Ordinal);
    }

    private static bool IsTemplateExpression(ReadOnlySpan<char> value)
    {
        ReadOnlySpan<char> list = !value.IsEmpty && UriTemplateOperators.Contains(value[0], StringComparison.Ordinal) ? value[1..] : value;

        if (list.IsEmpty)
        {
            return false;
        }

        foreach (Range range in list.Split(','))
        {
            if (!IsTemplateVariable(list[range]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTemplateVariable(ReadOnlySpan<char> value)
    {
        ReadOnlySpan<char> name = value;

        if (value.EndsWith("*"))
        {
            name = value[..^1];
        }
        else if (value.IndexOf(':') is >= 0 and var colon)
        {
            ReadOnlySpan<char> length = value[(colon + 1)..];

            if (length.Length is 0 or > 4 || length[0] == '0' || !TryDigits(length, out _))
            {
                return false;
            }

            name = value[..colon];
        }

        if (name.IsEmpty)
        {
            return false;
        }

        foreach (Range range in name.Split('.'))
        {
            if (!IsTemplateNamePart(name[range]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTemplateNamePart(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return false;
        }

        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] == '%')
            {
                if (!IsPercentEscape(value[index..]))
                {
                    return false;
                }

                index += 2;
            }
            else if (!char.IsAsciiLetterOrDigit(value[index]) && value[index] != '_')
            {
                return false;
            }
        }

        return true;
    }

    [GeneratedRegex(@"\AP(?:(?:[0-9]+D|[0-9]+M(?:[0-9]+D)?|[0-9]+Y(?:[0-9]+M(?:[0-9]+D)?)?)(?:T(?:[0-9]+H(?:[0-9]+M(?:[0-9]+S)?)?|[0-9]+M(?:[0-9]+S)?|[0-9]+S))?|T(?:[0-9]+H(?:[0-9]+M(?:[0-9]+S)?)?|[0-9]+M(?:[0-9]+S)?|[0-9]+S)|[0-9]+W)\z", RegexOptions.CultureInvariant)]
    private static partial Regex DurationShape();
}
