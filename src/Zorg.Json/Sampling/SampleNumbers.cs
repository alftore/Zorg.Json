using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Zorg.Json.Model;
using Zorg.Json.Validation;

namespace Zorg.Json.Sampling;

/// <summary>Numbers that fit a schema's bounds, computed exactly as a mantissa and a power of ten.</summary>
internal static class SampleNumbers
{
    /// <summary>Aligning two numbers expands them to about this many digits at most, because an exponent can be in the billions.</summary>
    private const int MaxExponentSpan = 20_000;

    /// <summary>Numbers written out in full stay below this many digits. Longer ones use an exponent.</summary>
    private const int MaxPlainDigits = 400;

    private static readonly Scaled One = new(BigInteger.One, 0);

    public static IEnumerable<string> Candidates(SchemaNode view, bool integer, int breadth)
    {
        Scaled? lower = Bound(view.Minimum, view.ExclusiveMinimum, highest: true);
        Scaled? upper = Bound(view.Maximum, view.ExclusiveMaximum, highest: false);
        Scaled? step = Read(view.MultipleOf) is { Mantissa.IsZero: false } multiple ? multiple.Absolute : null;

        if (integer)
        {
            step = step is { } divisor ? Lcm(divisor, One) ?? divisor : One;
        }

        return Raw(lower, upper, step, breadth).Where(text => Fits(view, integer, text));
    }

    public static JsonElement? Lcm(JsonElement left, JsonElement right)
    {
        Scaled first = Read(left)!.Value.Absolute;
        Scaled second = Read(right)!.Value.Absolute;

        if (first.Mantissa.IsZero || second.Mantissa.IsZero)
        {
            return left;
        }

        return Lcm(first, second) is { } result ? JsonElement.Parse(result.Text) : null;
    }

    private static IEnumerable<string> Raw(Scaled? lower, Scaled? upper, Scaled? step, int breadth)
    {
        yield return "0";

        if (lower is { } low)
        {
            yield return low.Text;
        }

        if (upper is { } high)
        {
            yield return high.Text;
        }

        foreach (Scaled value in Grid(lower, upper, step ?? One, breadth))
        {
            yield return value.Text;
        }

        if (step is not null)
        {
            yield break;
        }

        foreach (Scaled value in Between(lower, upper, breadth))
        {
            yield return value.Text;
        }
    }

    private static IEnumerable<Scaled> Grid(Scaled? lower, Scaled? upper, Scaled step, int breadth)
    {
        if (lower is null && upper is null)
        {
            foreach (int sign in (int[])[1, -1])
            {
                for (int index = 1; index <= breadth; index++)
                {
                    yield return new Scaled(step.Mantissa * index * sign, step.Exponent);
                }
            }
        }

        if (lower is { } low && Align(low, step, out BigInteger start, out BigInteger size, out int exponent))
        {
            BigInteger first = FloorDivide(start + size - 1, size);

            for (int index = 0; index <= breadth; index++)
            {
                yield return new Scaled((first + index) * size, exponent);
            }
        }

        if (upper is { } high && Align(high, step, out BigInteger end, out size, out exponent))
        {
            BigInteger last = FloorDivide(end, size);

            for (int index = 0; index <= breadth; index++)
            {
                yield return new Scaled((last - index) * size, exponent);
            }
        }
    }

    /// <summary>Fractions, for a range with no integer in it. The last digit moves first, then the range is split.</summary>
    private static IEnumerable<Scaled> Between(Scaled? lower, Scaled? upper, int breadth)
    {
        if (lower is { } from && upper is { } to && Align(from, to, out BigInteger start, out BigInteger end, out int exponent))
        {
            int power = (int)BigInteger.Log2(breadth + 1) + 1;
            BigInteger parts = BigInteger.One << power;
            BigInteger scale = BigInteger.Pow(5, power);

            foreach (int index in Enumerable.Range(1, breadth).OrderBy(index => BigInteger.Abs((index * 2) - parts)))
            {
                yield return new Scaled(((start * parts) + ((end - start) * index)) * scale, exponent - power);
            }
        }

        if (lower is { } low)
        {
            yield return new Scaled(low.Mantissa + 1, low.Exponent);
            yield return new Scaled((low.Mantissa * 10) + 1, low.Exponent - 1);
        }

        if (upper is { } high)
        {
            yield return new Scaled(high.Mantissa - 1, high.Exponent);
            yield return new Scaled((high.Mantissa * 10) - 1, high.Exponent - 1);
        }
    }

    private static bool Fits(SchemaNode view, bool integer, string text)
    {
        ExactNumber number = ExactNumber.Parse(text);

        return (!integer || number.IsInteger)
            && (view.Minimum is not { } minimum || number.CompareTo(Exact(minimum)) >= 0)
            && (view.Maximum is not { } maximum || number.CompareTo(Exact(maximum)) <= 0)
            && (view.ExclusiveMinimum is not { } above || number.CompareTo(Exact(above)) > 0)
            && (view.ExclusiveMaximum is not { } below || number.CompareTo(Exact(below)) < 0)
            && (view.MultipleOf is not { } divisor || number.IsMultipleOf(Exact(divisor)));
    }

    private static ExactNumber Exact(JsonElement value) => ExactNumber.Parse(value.GetRawText());

    private static Scaled? Read(JsonElement? value)
    {
        if (value is not { } number)
        {
            return null;
        }

        ExactNumber exact = Exact(number);
        return new Scaled(exact.Mantissa, exact.Exponent);
    }

    private static Scaled? Bound(JsonElement? inclusive, JsonElement? exclusive, bool highest)
    {
        if (inclusive is not { } first || exclusive is not { } second)
        {
            return Read(inclusive ?? exclusive);
        }

        return Read(Exact(first).CompareTo(Exact(second)) >= 0 == highest ? first : second);
    }

    private static Scaled? Lcm(Scaled left, Scaled right)
    {
        if (!Align(left, right, out BigInteger first, out BigInteger second, out int exponent))
        {
            return null;
        }

        return new Scaled(first / BigInteger.GreatestCommonDivisor(first, second) * second, exponent);
    }

    private static bool Align(Scaled left, Scaled right, out BigInteger first, out BigInteger second, out int exponent)
    {
        exponent = Math.Min(left.Exponent, right.Exponent);

        if ((long)Math.Max(left.Exponent, right.Exponent) - exponent > MaxExponentSpan)
        {
            first = second = BigInteger.Zero;
            return false;
        }

        first = left.Mantissa * BigInteger.Pow(10, left.Exponent - exponent);
        second = right.Mantissa * BigInteger.Pow(10, right.Exponent - exponent);
        return true;
    }

    private static BigInteger FloorDivide(BigInteger dividend, BigInteger divisor)
    {
        BigInteger quotient = BigInteger.DivRem(dividend, divisor, out BigInteger remainder);
        return remainder.Sign < 0 ? quotient - 1 : quotient;
    }

    private readonly record struct Scaled(BigInteger Mantissa, int Exponent)
    {
        public Scaled Absolute => new(BigInteger.Abs(Mantissa), Exponent);

        public string Text
        {
            get
            {
                if (Mantissa.IsZero)
                {
                    return "0";
                }

                string sign = Mantissa.Sign < 0 ? "-" : string.Empty;
                string digits = BigInteger.Abs(Mantissa).ToString(CultureInfo.InvariantCulture);
                long point = (long)digits.Length + Exponent;

                if (Exponent >= 0)
                {
                    return point <= MaxPlainDigits ? sign + digits + new string('0', Exponent) : Scientific(sign, digits);
                }

                if (point > 0)
                {
                    int split = (int)point;
                    string fraction = digits[split..].TrimEnd('0');
                    return sign + digits[..split] + (fraction.Length == 0 ? string.Empty : $".{fraction}");
                }

                return -point <= MaxPlainDigits ? $"{sign}0.{new string('0', (int)-point)}{digits.TrimEnd('0')}" : Scientific(sign, digits);
            }
        }

        private string Scientific(string sign, string digits)
        {
            string significant = digits.TrimEnd('0');
            long exponent = (long)Exponent + digits.Length - significant.Length;
            return string.Create(CultureInfo.InvariantCulture, $"{sign}{significant}e{exponent}");
        }
    }
}
