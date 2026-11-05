using System;
using System.Globalization;
using System.Numerics;

namespace Zorg.Json.Validation;

/// <summary>
/// A JSON number as significant digits times a power of ten. JSON numbers have no size limit, and
/// doubles can't compare them or test <c>multipleOf</c> exactly. The digits stay text, so that comparing two numbers
/// takes time linear in their length, which big integer arithmetic doesn't.
/// </summary>
/// <remarks>
/// The exponent is held exactly when it has at most <see cref="MaxExponentDigits"/> digits. A number with a longer exponent
/// is <see cref="IsOutOfRange"/>: its sign is right, and nothing else is. Callers decide what that means, which for
/// validation is a failed parse.
/// </remarks>
internal readonly struct ExactNumber
{
    /// <summary>The most digits an exponent may have. The largest one, 10^18 - 1, leaves room for adding the digit counts in a <see cref="long"/>.</summary>
    public const int MaxExponentDigits = 18;

    private const long ExponentLimit = int.MaxValue / 2;
    private const int ChunkDigits = 18;
    private static readonly BigInteger ChunkScale = BigInteger.Pow(10, ChunkDigits);

    private readonly string _digits;
    private readonly int _sign;
    private readonly long _exponent;
    private readonly bool _outOfRange;

    private ExactNumber(int sign, string digits, long exponent, bool outOfRange)
    {
        _sign = sign;
        _digits = digits;
        _exponent = exponent;
        _outOfRange = outOfRange;
    }

    public bool IsInteger => _sign == 0 || _exponent >= 0;

    public bool IsZero => _sign == 0;

    /// <summary>True when the exponent has too many digits to hold. Only the sign is meaningful then.</summary>
    public bool IsOutOfRange => _outOfRange;

    public BigInteger Mantissa => _sign == 0 ? BigInteger.Zero : _sign * BigInteger.Parse(_digits, NumberStyles.None, CultureInfo.InvariantCulture);

    /// <summary>The exponent, held within an <see cref="int"/>. Use <see cref="LongExponent"/> to tell large exponents apart.</summary>
    public int Exponent => (int)Math.Clamp(_exponent, -ExponentLimit, ExponentLimit);

    public long LongExponent => _exponent;

    /// <summary>Parses a JSON number. An exponent with too many digits gives a number that is <see cref="IsOutOfRange"/>.</summary>
    public static ExactNumber Parse(string text) =>
        TryParse(text, out ExactNumber number, out _) ? number : throw new FormatException($"Not a JSON number: {text}");

    /// <summary>False when the text isn't a JSON number, or its exponent has too many digits.</summary>
    public static bool TryParse(string text, out ExactNumber number) =>
        TryParse(text, out number, out _) && !number._outOfRange;

    /// <summary>False when the text isn't a JSON number. A number with too many exponent digits is read, and <see cref="IsOutOfRange"/> is set.</summary>
    public static bool TryParse(string text, out ExactNumber number, out bool outOfRange)
    {
        number = default;
        outOfRange = false;
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

        int integerEnd = position;
        int fractionStart = position;
        int fractionEnd = position;
        long exponent = 0;

        if (position < text.Length && text[position] == '.')
        {
            fractionStart = ++position;
            SkipDigits(text, ref position);

            if (position == fractionStart)
            {
                return false;
            }

            fractionEnd = position;
            exponent -= fractionEnd - fractionStart;
        }

        ReadOnlySpan<char> written = default;
        bool negativeExponent = false;

        if (position < text.Length && text[position] is 'e' or 'E')
        {
            position++;
            negativeExponent = position < text.Length && text[position] == '-';

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

            written = text.AsSpan(exponentStart, position - exponentStart).TrimStart('0');
        }

        if (position != text.Length)
        {
            return false;
        }

        // The digits are the integer and fraction parts without the zeros at either end.
        string digits = fractionEnd == fractionStart ? text[integerStart..integerEnd] : string.Concat(text.AsSpan(integerStart, integerEnd - integerStart), text.AsSpan(fractionStart, fractionEnd - fractionStart));
        string trimmed = digits.TrimEnd('0');
        exponent += digits.Length - trimmed.Length;
        trimmed = trimmed.TrimStart('0');

        if (trimmed.Length == 0)
        {
            number = new ExactNumber(0, string.Empty, 0, outOfRange: false);
            return true;
        }

        int sign = negative ? -1 : 1;

        if (written.Length > MaxExponentDigits)
        {
            outOfRange = true;
            number = new ExactNumber(sign, trimmed, negativeExponent ? -ExponentLimit : ExponentLimit, outOfRange: true);
            return true;
        }

        long value = written.IsEmpty ? 0 : long.Parse(written, NumberStyles.None, CultureInfo.InvariantCulture);
        exponent += negativeExponent ? -value : value;
        number = new ExactNumber(sign, trimmed, exponent, outOfRange: false);
        return true;
    }

    /// <summary>True when the text is a number whose digits are all zero, however large its exponent.</summary>
    public static bool IsZeroText(string text)
    {
        foreach (char character in text)
        {
            if (character is 'e' or 'E')
            {
                break;
            }

            if (character is >= '1' and <= '9')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The order of two numbers that are in range, in time linear in their length.</summary>
    public int CompareTo(ExactNumber other)
    {
        int sign = _sign.CompareTo(other._sign);

        if (sign != 0 || _sign == 0)
        {
            return sign;
        }

        long magnitude = _digits.Length + _exponent;
        long otherMagnitude = other._digits.Length + other._exponent;

        if (magnitude != otherMagnitude)
        {
            return magnitude.CompareTo(otherMagnitude) * _sign;
        }

        // The same magnitude means the digits line up. Neither ends in a zero, so a shorter run of digits is the smaller number.
        return _digits.AsSpan().SequenceCompareTo(other._digits.AsSpan()) switch
        {
            < 0 => -_sign,
            > 0 => _sign,
            _ => 0,
        };
    }

    /// <summary>A hash that agrees with <see cref="CompareTo"/>: numbers that are equal hash alike.</summary>
    public int GetHash() => _outOfRange ? _sign : HashCode.Combine(_sign, string.GetHashCode(_digits, StringComparison.Ordinal), _exponent);

    /// <summary>The cost of <see cref="IsMultipleOf"/> in steps, to charge before calling it.</summary>
    public long MultipleOfCost(ExactNumber divisor) => ((_digits.Length / ChunkDigits) + 1) * ((divisor._digits.Length / ChunkDigits) + 1);

    public bool IsMultipleOf(ExactNumber divisor)
    {
        if (_sign == 0)
        {
            return true;
        }

        if (divisor._sign == 0)
        {
            return false;
        }

        long shift = _exponent - divisor._exponent;

        // The digits don't end in a zero, so they can't be divided by a power of ten.
        if (shift < 0)
        {
            return false;
        }

        BigInteger modulus = BigInteger.Parse(divisor._digits, NumberStyles.None, CultureInfo.InvariantCulture);
        BigInteger remainder = Remainder(_digits, modulus);
        return (BigInteger.ModPow(10, shift, modulus) * remainder % modulus).IsZero;
    }

    private static BigInteger Remainder(string digits, BigInteger modulus)
    {
        BigInteger remainder = BigInteger.Zero;

        for (int index = 0; index < digits.Length; index += ChunkDigits)
        {
            int length = Math.Min(ChunkDigits, digits.Length - index);
            long chunk = long.Parse(digits.AsSpan(index, length), NumberStyles.None, CultureInfo.InvariantCulture);
            remainder = ((remainder * (length == ChunkDigits ? ChunkScale : BigInteger.Pow(10, length))) + chunk) % modulus;
        }

        return remainder;
    }

    private static void SkipDigits(string text, ref int position)
    {
        while (position < text.Length && char.IsAsciiDigit(text[position]))
        {
            position++;
        }
    }
}
