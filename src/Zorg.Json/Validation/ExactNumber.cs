using System;
using System.Globalization;
using System.Numerics;

namespace Zorg.Json.Validation;

/// <summary>
/// A JSON number as mantissa times a power of ten. JSON numbers have no size limit, and
/// doubles can't compare them or test <c>multipleOf</c> exactly.
/// </summary>
internal readonly struct ExactNumber
{
    private const long ExponentLimit = int.MaxValue / 2;

    private readonly BigInteger _mantissa;
    private readonly int _exponent;
    private readonly int _digits;

    private ExactNumber(BigInteger mantissa, int exponent, int digits)
    {
        _mantissa = mantissa;
        _exponent = exponent;
        _digits = digits;
    }

    public bool IsInteger => _mantissa.IsZero || _exponent >= 0;

    public BigInteger Mantissa => _mantissa;

    public int Exponent => _exponent;

    public static ExactNumber Parse(string text) =>
        TryParse(text, out ExactNumber number) ? number : throw new FormatException($"Not a JSON number: {text}");

    public static bool TryParse(string text, out ExactNumber number)
    {
        number = default;
        int position = 0;
        bool negative = position < text.Length && text[position] == '-';

        if (negative)
        {
            position++;
        }

        int integerStart = position;
        SkipDigits(text, ref position);
        int integerLength = position - integerStart;

        if (integerLength == 0 || (integerLength > 1 && text[integerStart] == '0'))
        {
            return false;
        }

        string digits = text[integerStart..position];
        long exponent = 0;

        if (position < text.Length && text[position] == '.')
        {
            int fractionStart = ++position;
            SkipDigits(text, ref position);

            if (position == fractionStart)
            {
                return false;
            }

            digits += text[fractionStart..position];
            exponent -= position - fractionStart;
        }

        if (position < text.Length && text[position] is 'e' or 'E')
        {
            position++;
            bool negativeExponent = position < text.Length && text[position] == '-';

            if (position < text.Length && text[position] is '+' or '-')
            {
                position++;
            }

            int exponentStart = position;
            SkipDigits(text, ref position);

            if (position == exponentStart)
            {
                return false;
            }

            long written = long.TryParse(text.AsSpan(exponentStart, position - exponentStart), NumberStyles.None, CultureInfo.InvariantCulture, out long parsed)
                ? Math.Min(parsed, ExponentLimit)
                : ExponentLimit;

            exponent += negativeExponent ? -written : written;
        }

        if (position != text.Length)
        {
            return false;
        }

        string trimmed = digits.TrimEnd('0');
        exponent += digits.Length - trimmed.Length;
        trimmed = trimmed.TrimStart('0');

        if (trimmed.Length == 0)
        {
            number = new ExactNumber(BigInteger.Zero, 0, 0);
            return true;
        }

        BigInteger mantissa = BigInteger.Parse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture);
        int clamped = (int)Math.Clamp(exponent, -ExponentLimit, ExponentLimit);
        number = new ExactNumber(negative ? -mantissa : mantissa, clamped, trimmed.Length);
        return true;
    }

    public int CompareTo(ExactNumber other)
    {
        int sign = _mantissa.Sign.CompareTo(other._mantissa.Sign);

        if (sign != 0 || _mantissa.IsZero)
        {
            return sign;
        }

        long magnitude = (long)_digits + _exponent;
        long otherMagnitude = (long)other._digits + other._exponent;

        if (magnitude != otherMagnitude)
        {
            return magnitude.CompareTo(otherMagnitude) * _mantissa.Sign;
        }

        // Equal magnitudes mean the exponents differ by no more than the digit counts do.
        int shift = _exponent - other._exponent;
        return shift >= 0
            ? (_mantissa * BigInteger.Pow(10, shift)).CompareTo(other._mantissa)
            : _mantissa.CompareTo(other._mantissa * BigInteger.Pow(10, -shift));
    }

    public bool IsMultipleOf(ExactNumber divisor)
    {
        if (_mantissa.IsZero)
        {
            return true;
        }

        BigInteger absolute = BigInteger.Abs(divisor._mantissa);
        long shift = (long)_exponent - divisor._exponent;

        if (shift >= 0)
        {
            return (BigInteger.ModPow(10, shift, absolute) * BigInteger.Abs(_mantissa) % absolute).IsZero;
        }

        return -shift < _digits && (BigInteger.Abs(_mantissa) % (absolute * BigInteger.Pow(10, (int)-shift))).IsZero;
    }

    private static void SkipDigits(string text, ref int position)
    {
        while (position < text.Length && char.IsAsciiDigit(text[position]))
        {
            position++;
        }
    }
}
