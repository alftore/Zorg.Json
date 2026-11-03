using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Zorg.Json.Validation;

/// <summary>
/// Host names under RFC 1123 and IDNA2008 (RFC 5890 to 5893). .NET has no public Bidi class, joining type or script
/// data, so those come from the Unicode blocks of the scripts involved, which is close but not exact for rare characters.
/// </summary>
internal static class Idna
{
    private const int MaxLabelLength = 63;
    private const int MaxNameLength = 253;
    private const string AcePrefix = "xn--";

    private static readonly FrozenSet<int> Viramas = new[]
    {
        0x094D, 0x09CD, 0x0A4D, 0x0ACD, 0x0B4D, 0x0BCD, 0x0C4D, 0x0CCD, 0x0D3B, 0x0D3C, 0x0D4D, 0x0DCA, 0x0E3A, 0x0E4E, 0x0EBA, 0x0F84,
        0x1039, 0x103A, 0x1714, 0x1734, 0x17D2, 0x1A60, 0x1B44, 0x1BAA, 0x1BAB, 0x1BF2, 0x1BF3, 0x2D7F, 0xA806, 0xA82C, 0xA8C4, 0xA953,
        0xA9C0, 0xAAF6, 0xABED, 0x10A3F, 0x11046, 0x11070, 0x1107F, 0x110B9, 0x11133, 0x11134, 0x111C0, 0x11235, 0x112EA, 0x1134D, 0x11442,
        0x114C2, 0x115BF, 0x1163F, 0x116B6, 0x1172B, 0x11839, 0x1193D, 0x1193E, 0x119E0, 0x11A34, 0x11A47, 0x11A99, 0x11C3F, 0x11D44, 0x11D45, 0x11D97,
    }.ToFrozenSet();

    private enum BidiClass
    {
        LeftToRight,
        RightToLeft,
        ArabicNumber,
        EuropeanNumber,
        NonSpacingMark,
        Neutral,
    }

    public static bool IsHostname(string value)
    {
        if (value.Length is 0 or > MaxNameLength)
        {
            return false;
        }

        List<string> labels = [];

        foreach (string label in value.Split('.'))
        {
            if (!IsLdhLabel(label))
            {
                return false;
            }

            if (!TryDecodeAce(label, out string decoded))
            {
                return false;
            }

            labels.Add(decoded);
        }

        return SatisfiesBidiRule(labels);
    }

    public static bool IsIdnHostname(string value)
    {
        string? mapped = Map(value);

        if (mapped is null || mapped.Length == 0)
        {
            return false;
        }

        List<string> labels = [];
        int length = -1;

        foreach (string label in mapped.Split('.'))
        {
            if (!TryCheckLabel(label, out string decoded, out int encodedLength))
            {
                return false;
            }

            labels.Add(decoded);
            length += encodedLength + 1;
        }

        return length <= MaxNameLength && SatisfiesBidiRule(labels);
    }

    /// <summary>The UTS 46 mapping step: separators, ignored characters, width and case.</summary>
    private static string? Map(string value)
    {
        StringBuilder mapped = new(value.Length);

        foreach (char character in value)
        {
            if (character is '。' or '．' or '｡')
            {
                mapped.Append('.');
            }
            else if (character is not ('­' or '͏' or '​' or '⁠' or '⁤' or '﻿') && character is not (>= '︀' and <= '️'))
            {
                mapped.Append(character);
            }
        }

        try
        {
            string folded = mapped.ToString().Normalize(NormalizationForm.FormKC);
            return ToLower(folded).Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string ToLower(string value)
    {
#pragma warning disable CA1308 // UTS 46 maps to lower case.
        return value.ToLowerInvariant();
#pragma warning restore CA1308
    }

    private static bool TryCheckLabel(string label, out string decoded, out int encodedLength)
    {
        decoded = label;
        encodedLength = label.Length;

        if (label.Length == 0)
        {
            return false;
        }

        if (label.All(char.IsAscii))
        {
            return IsLdhLabel(label) && TryDecodeAce(label, out decoded);
        }

        if (!IsValidULabel(label) || !Punycode.TryEncode(label, out string encoded))
        {
            return false;
        }

        encodedLength = AcePrefix.Length + encoded.Length;
        return encodedLength <= MaxLabelLength;
    }

    private static bool IsLdhLabel(string label) =>
        label.Length is > 0 and <= MaxLabelLength
        && label[0] != '-'
        && label[^1] != '-'
        && label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private static bool TryDecodeAce(string label, out string decoded)
    {
        decoded = label;

        if (!label.StartsWith(AcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string body = label[AcePrefix.Length..];

        if (!Punycode.TryDecode(body, out decoded)
            || decoded.All(char.IsAscii)
            || !Punycode.TryEncode(decoded, out string encoded)
            || !encoded.Equals(body, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsValidULabel(decoded);
    }

    private static bool IsValidULabel(string label)
    {
        int[] points = [.. label.EnumerateRunes().Select(rune => rune.Value)];

        if (points.Length == 0 || !label.IsNormalized(NormalizationForm.FormC) || label[0] == '-' || label[^1] == '-'
            || (label.Length >= 4 && label[2] == '-' && label[3] == '-'))
        {
            return false;
        }

        if (CharUnicodeInfo.GetUnicodeCategory(points[0]) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
        {
            return false;
        }

        for (int index = 0; index < points.Length; index++)
        {
            if (!IsAllowed(points, index))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The derivation in RFC 5892 section 3, in its order of precedence.</summary>
    private static bool IsAllowed(int[] points, int index)
    {
        int point = points[index];

        switch (point)
        {
            case 0xDF or 0x3C2 or 0x6FD or 0x6FE or 0xF0B or 0x3007:
                return true;

            case 0xB7:
                return index > 0 && index + 1 < points.Length && points[index - 1] == 'l' && points[index + 1] == 'l';

            case 0x375:
                return index + 1 < points.Length && IsGreek(points[index + 1]);

            case 0x5F3 or 0x5F4:
                return index > 0 && IsHebrew(points[index - 1]);

            case 0x30FB:
                return points.Any(IsJapanese);

            case >= 0x660 and <= 0x669:
                return !points.Any(other => other is >= 0x6F0 and <= 0x6F9);

            case >= 0x6F0 and <= 0x6F9:
                return !points.Any(other => other is >= 0x660 and <= 0x669);

            case 0x640 or 0x7FA or 0x302E or 0x302F or (>= 0x3031 and <= 0x3035) or 0x303B:
                return false;

            case 0x200C:
                return FollowsVirama(points, index) || JoinsArabic(points, index);

            case 0x200D:
                return FollowsVirama(points, index);
        }

        if (point < 0x80)
        {
            return char.IsAsciiLetterLower((char)point) || char.IsAsciiDigit((char)point) || point == '-';
        }

        bool isExcluded = point is (>= 0x1100 and <= 0x11FF) or (>= 0xA960 and <= 0xA97F) or (>= 0xD7B0 and <= 0xD7FF)
            or (>= 0x20D0 and <= 0x20FF) or (>= 0x1D100 and <= 0x1D24F) or 0x34F or (>= 0x180B and <= 0x180F) or (>= 0xE0100 and <= 0xE01EF);

        return !isExcluded
            && CharUnicodeInfo.GetUnicodeCategory(point) is UnicodeCategory.LowercaseLetter or UnicodeCategory.UppercaseLetter or UnicodeCategory.OtherLetter
                or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.ModifierLetter or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            && IsStable(point);
    }

    private static bool IsStable(int point)
    {
        string text = char.ConvertFromUtf32(point);
        return string.Equals(text, ToLower(text.Normalize(NormalizationForm.FormKC)).Normalize(NormalizationForm.FormKC), StringComparison.Ordinal);
    }

    private static bool IsGreek(int point) => point is (>= 0x370 and <= 0x3FF) or (>= 0x1F00 and <= 0x1FFF);

    private static bool IsHebrew(int point) => point is (>= 0x591 and <= 0x5F4) or (>= 0xFB1D and <= 0xFB4F);

    private static bool IsJapanese(int point) =>
        point is not 0x30FB
        && point is (>= 0x3040 and <= 0x30FF) or (>= 0x31F0 and <= 0x31FF) or (>= 0xFF66 and <= 0xFF9F) or 0x3005 or 0x3007
            or (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF) or (>= 0xF900 and <= 0xFAFF) or (>= 0x20000 and <= 0x3FFFF);

    private static bool FollowsVirama(int[] points, int index) => index > 0 && Viramas.Contains(points[index - 1]);

    /// <summary>The regular expression of RFC 5892 A.1, with Arabic-script letters standing in for joining types L, R and D.</summary>
    private static bool JoinsArabic(int[] points, int index)
    {
        int before = index - 1;

        while (before >= 0 && IsTransparent(points[before]))
        {
            before--;
        }

        int after = index + 1;

        while (after < points.Length && IsTransparent(points[after]))
        {
            after++;
        }

        return before >= 0 && after < points.Length && IsJoining(points[before]) && IsJoining(points[after]);
    }

    private static bool IsTransparent(int point) =>
        CharUnicodeInfo.GetUnicodeCategory(point) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format;

    private static bool IsJoining(int point) =>
        point is (>= 0x620 and <= 0x74F) or (>= 0x750 and <= 0x77F) or (>= 0x8A0 and <= 0x8FF) or (>= 0xA840 and <= 0xA872) or (>= 0x1800 and <= 0x18AF)
        && CharUnicodeInfo.GetUnicodeCategory(point) == UnicodeCategory.OtherLetter;

    private static BidiClass ToBidiClass(int point)
    {
        UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(point);

        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)
        {
            return BidiClass.NonSpacingMark;
        }

        if (point is (>= 0x660 and <= 0x669) or 0x66B or 0x66C or 0x6DD)
        {
            return BidiClass.ArabicNumber;
        }

        if (point is (>= '0' and <= '9') or (>= 0x6F0 and <= 0x6F9))
        {
            return BidiClass.EuropeanNumber;
        }

        if (point is (>= 0x590 and <= 0x5FF) or (>= 0x7C0 and <= 0x85F) or (>= 0xFB1D and <= 0xFB4F) or (>= 0x10800 and <= 0x10FFF) or (>= 0x1E800 and <= 0x1EFFF))
        {
            return BidiClass.RightToLeft;
        }

        if (point is (>= 0x600 and <= 0x7BF) or (>= 0x860 and <= 0x8FF) or (>= 0xFB50 and <= 0xFDFF) or (>= 0xFE70 and <= 0xFEFF))
        {
            return category is UnicodeCategory.OtherLetter or UnicodeCategory.ModifierLetter ? BidiClass.RightToLeft : BidiClass.Neutral;
        }

        return category is UnicodeCategory.LowercaseLetter or UnicodeCategory.UppercaseLetter or UnicodeCategory.OtherLetter or UnicodeCategory.ModifierLetter
            or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber
            ? BidiClass.LeftToRight
            : BidiClass.Neutral;
    }

    private static bool SatisfiesBidiRule(List<string> labels)
    {
        BidiClass[][] classes = [.. labels.Select(label => label.EnumerateRunes().Select(rune => ToBidiClass(rune.Value)).ToArray())];
        bool isBidiDomain = classes.Any(label => label.Any(type => type is BidiClass.RightToLeft or BidiClass.ArabicNumber));
        return !isBidiDomain || classes.All(SatisfiesBidiRule);
    }

    /// <summary>The six conditions of RFC 5893 section 2.</summary>
    private static bool SatisfiesBidiRule(BidiClass[] label)
    {
        BidiClass first = label[0];

        if (first is not (BidiClass.LeftToRight or BidiClass.RightToLeft))
        {
            return false;
        }

        BidiClass last = label.Last(type => type != BidiClass.NonSpacingMark);

        if (first == BidiClass.RightToLeft)
        {
            bool hasBothNumbers = label.Contains(BidiClass.ArabicNumber) && label.Contains(BidiClass.EuropeanNumber);
            return !label.Contains(BidiClass.LeftToRight) && !hasBothNumbers && last != BidiClass.Neutral;
        }

        return !label.Contains(BidiClass.RightToLeft) && !label.Contains(BidiClass.ArabicNumber) && last is BidiClass.LeftToRight or BidiClass.EuropeanNumber;
    }

    /// <summary>RFC 3492.</summary>
    private static class Punycode
    {
        private const int Base = 36;
        private const int TMin = 1;
        private const int TMax = 26;
        private const int Skew = 38;
        private const int Damp = 700;
        private const int InitialBias = 72;
        private const int InitialN = 128;

        public static bool TryDecode(string input, out string output)
        {
            output = string.Empty;
            int delimiter = input.LastIndexOf('-');
            List<int> points = [];

            for (int index = 0; index < delimiter; index++)
            {
                points.Add(input[index]);
            }

            int position = delimiter < 0 ? 0 : delimiter + 1;
            long n = InitialN;
            long i = 0;
            int bias = InitialBias;

            while (position < input.Length)
            {
                long old = i;
                long weight = 1;

                for (int k = Base; ; k += Base)
                {
                    if (position == input.Length || !TryDigit(input[position++], out int digit))
                    {
                        return false;
                    }

                    i += digit * weight;

                    if (i > int.MaxValue)
                    {
                        return false;
                    }

                    int threshold = Threshold(k, bias);

                    if (digit < threshold)
                    {
                        break;
                    }

                    weight *= Base - threshold;
                }

                int length = points.Count + 1;
                bias = Adapt(i - old, length, old == 0);
                n += i / length;
                i %= length;

                if (n > 0x10FFFF || n is >= 0xD800 and <= 0xDFFF)
                {
                    return false;
                }

                points.Insert((int)i, (int)n);
                i++;
            }

            StringBuilder decoded = new(points.Count);

            foreach (int point in points)
            {
                decoded.Append(char.ConvertFromUtf32(point));
            }

            output = decoded.ToString();
            return true;
        }

        public static bool TryEncode(string input, out string output)
        {
            int[] points = [.. input.EnumerateRunes().Select(rune => rune.Value)];
            StringBuilder encoded = new();
            encoded.Append(points.Where(point => point < InitialN).Select(point => (char)point).ToArray());
            int basic = encoded.Length;
            int handled = basic;

            if (basic > 0)
            {
                encoded.Append('-');
            }

            long n = InitialN;
            long delta = 0;
            int bias = InitialBias;

            while (handled < points.Length)
            {
                long next = points.Where(point => point >= n).Min();
                delta += (next - n) * (handled + 1);
                n = next;

                foreach (int point in points)
                {
                    if (point < n)
                    {
                        delta++;
                    }
                    else if (point == n)
                    {
                        AppendVariableLength(encoded, delta, bias);
                        bias = Adapt(delta, handled + 1, handled == basic);
                        delta = 0;
                        handled++;
                    }
                }

                delta++;
                n++;
            }

            output = encoded.ToString();
            return true;
        }

        private static void AppendVariableLength(StringBuilder encoded, long delta, int bias)
        {
            long q = delta;

            for (int k = Base; ; k += Base)
            {
                int threshold = Threshold(k, bias);

                if (q < threshold)
                {
                    break;
                }

                encoded.Append(ToDigit(threshold + (int)((q - threshold) % (Base - threshold))));
                q = (q - threshold) / (Base - threshold);
            }

            encoded.Append(ToDigit((int)q));
        }

        private static int Threshold(int k, int bias) => k <= bias ? TMin : k >= bias + TMax ? TMax : k - bias;

        private static int Adapt(long delta, int length, bool isFirst)
        {
            long adjusted = isFirst ? delta / Damp : delta / 2;
            adjusted += adjusted / length;
            int k = 0;

            while (adjusted > ((Base - TMin) * TMax) / 2)
            {
                adjusted /= Base - TMin;
                k += Base;
            }

            return k + (int)(((Base - TMin + 1) * adjusted) / (adjusted + Skew));
        }

        private static char ToDigit(int digit) => (char)(digit < 26 ? 'a' + digit : '0' + digit - 26);

        private static bool TryDigit(char character, out int digit)
        {
            digit = character switch
            {
                >= 'a' and <= 'z' => character - 'a',
                >= 'A' and <= 'Z' => character - 'A',
                >= '0' and <= '9' => character - '0' + 26,
                _ => -1,
            };

            return digit >= 0;
        }
    }
}
