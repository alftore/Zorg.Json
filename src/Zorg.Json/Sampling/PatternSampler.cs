using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Zorg.Json.Validation;

namespace Zorg.Json.Sampling;

/// <summary>
/// Writes strings that match an ECMA-262 pattern, for the common subset: literals, classes, <c>.</c>, quantifiers,
/// groups, alternation and anchors. A pattern with a lookaround or a backreference gives no strings at all.
/// </summary>
internal static class PatternSampler
{
    private const int MaxNesting = 200;

    /// <summary>What a set such as <c>\d</c>, <c>[^a]</c> or <c>\p{Lu}</c> is tried against, in order of preference.</summary>
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 _-.,:;/+=@#$%&*!?~^()[]{}<>|'\"\\\t\n\réßΩЖ中😀";

    public static IEnumerable<string> Generate(string pattern, int minimum, int maximum, int breadth)
    {
        Part root;

        try
        {
            root = new Parser(pattern).Parse();
        }
        catch (FormatException)
        {
            return [];
        }

        return Strings(root, pattern, minimum, maximum, breadth);
    }

    private static IEnumerable<string> Strings(Part root, string pattern, int minimum, int maximum, int breadth)
    {
        for (int variant = 0; variant < breadth; variant++)
        {
            if (Write(root, pattern, minimum, variant) is { } text && JsonStrings.CodePointCount(text) <= maximum)
            {
                yield return text;
            }
        }

        // Longer strings, for uniqueItems and for what a short string can't satisfy.
        for (int extra = 1; extra < breadth; extra++)
        {
            if (Write(root, pattern, minimum + extra, 0) is { } text && JsonStrings.CodePointCount(text) <= maximum)
            {
                yield return text;
            }
        }
    }

    private static string? Write(Part root, string pattern, int minimum, int variant)
    {
        Writer shortest = new(variant, need: 0);
        root.Write(shortest);

        if (shortest.Overflowed)
        {
            return null;
        }

        int missing = minimum - shortest.Length;

        if (missing <= 0)
        {
            return shortest.Text;
        }

        Writer longer = new(variant, missing);
        root.Write(longer);

        if (longer.Overflowed)
        {
            return null;
        }

        string padding = new('a', Math.Max(0, longer.Need));

        if (!pattern.EndsWith('$'))
        {
            return longer.Text + padding;
        }

        return pattern.StartsWith('^') ? longer.Text : padding + longer.Text;
    }

    private sealed class Writer(int variant, int need)
    {
        private readonly StringBuilder _text = new();

        public int Variant { get; set; } = variant;

        public int Need { get; set; } = need;

        public int Length { get; private set; }

        public bool Overflowed => Length > SampleStrings.MaxLength;

        public string Text => _text.ToString();

        public void Append(int codePoint)
        {
            _text.Append(char.ConvertFromUtf32(codePoint));
            Length++;
        }
    }

    private abstract class Part
    {
        public abstract void Write(Writer writer);
    }

    private sealed class Nothing : Part
    {
        public override void Write(Writer writer)
        {
        }
    }

    private sealed class Characters(int[] members) : Part
    {
        public override void Write(Writer writer) => writer.Append(members[writer.Variant % members.Length]);
    }

    private sealed class Sequence(List<Part> parts) : Part
    {
        public override void Write(Writer writer)
        {
            foreach (Part part in parts)
            {
                part.Write(writer);
            }
        }
    }

    private sealed class Choice(List<Part> branches) : Part
    {
        public override void Write(Writer writer)
        {
            int branch = writer.Variant % branches.Count;
            writer.Variant /= branches.Count;
            branches[branch].Write(writer);
        }
    }

    private sealed class Repeat(Part part, int minimum, int maximum) : Part
    {
        public override void Write(Writer writer)
        {
            for (int count = 0; !writer.Overflowed && (count < minimum || (writer.Need > 0 && count < maximum)); count++)
            {
                int length = writer.Length;
                int need = writer.Need;
                int variant = writer.Variant;
                part.Write(writer);
                writer.Variant = variant;

                if (count >= minimum)
                {
                    int written = writer.Length - length;
                    writer.Need -= written - (need - writer.Need);

                    if (written == 0)
                    {
                        break;
                    }
                }
            }
        }
    }

    private sealed class Parser(string text)
    {
        private int _at;
        private int _nesting;

        private bool More => _at < text.Length;

        public Part Parse()
        {
            Part root = Alternatives();
            return More ? throw Unsupported() : root;
        }

        private static FormatException Unsupported() => new("This part of the pattern isn't supported.");

        private static bool IsSurrogate(int codePoint) => codePoint is >= 0xD800 and <= 0xDFFF;

        private static Characters Literal(int codePoint) =>
            IsSurrogate(codePoint) ? throw Unsupported() : new Characters([codePoint]);

        private static Characters Set(string source, IEnumerable<int> listed)
        {
            Regex regex = PatternCompiler.Compile($"^(?:{source})$");
            IEnumerable<int> alphabet = Alphabet.EnumerateRunes().Select(rune => rune.Value);
            int[] members = [.. listed.Concat(alphabet).Distinct().Where(member => !IsSurrogate(member) && regex.IsMatch(char.ConvertFromUtf32(member)))];
            return members.Length == 0 ? throw Unsupported() : new Characters(members);
        }

        private Part Alternatives()
        {
            if (++_nesting > MaxNesting)
            {
                throw Unsupported();
            }

            List<Part> branches = [Items()];

            while (More && text[_at] == '|')
            {
                _at++;
                branches.Add(Items());
            }

            _nesting--;
            return branches.Count == 1 ? branches[0] : new Choice(branches);
        }

        private Sequence Items()
        {
            List<Part> parts = [];

            while (More && text[_at] is not ('|' or ')'))
            {
                parts.Add(Quantified());
            }

            return new Sequence(parts);
        }

        private Part Quantified()
        {
            Part atom = Atom();

            if (!More || text[_at] is not ('*' or '+' or '?' or '{'))
            {
                return atom;
            }

            (int minimum, int maximum) = Bounds();

            if (More && text[_at] == '?')
            {
                _at++;
            }

            return new Repeat(atom, minimum, maximum);
        }

        private (int Minimum, int Maximum) Bounds()
        {
            char quantifier = text[_at++];

            if (quantifier != '{')
            {
                return quantifier switch
                {
                    '*' => (0, int.MaxValue),
                    '+' => (1, int.MaxValue),
                    _ => (0, 1),
                };
            }

            int minimum = Count();
            int maximum = minimum;

            if (More && text[_at] == ',')
            {
                _at++;
                maximum = More && text[_at] == '}' ? int.MaxValue : Count();
            }

            if (!More || text[_at] != '}')
            {
                throw Unsupported();
            }

            _at++;
            return (minimum, maximum);
        }

        private int Count()
        {
            int start = _at;

            while (More && char.IsAsciiDigit(text[_at]))
            {
                _at++;
            }

            if (start == _at)
            {
                throw Unsupported();
            }

            return int.TryParse(text.AsSpan(start, _at - start), NumberStyles.None, CultureInfo.InvariantCulture, out int count) ? count : int.MaxValue;
        }

        private Part Atom()
        {
            switch (text[_at])
            {
                case '^' or '$':
                    _at++;
                    return new Nothing();

                case '(':
                    return Group();

                case '[':
                    return Class();

                case '.':
                    _at++;
                    return Set(".", []);

                case '\\':
                    return Escape();

                default:
                    return Literal(NextCodePoint());
            }
        }

        private int NextCodePoint()
        {
            int codePoint = char.IsSurrogatePair(text, _at) ? char.ConvertToUtf32(text, _at) : text[_at];
            _at += codePoint > 0xFFFF ? 2 : 1;
            return codePoint;
        }

        private Part Group()
        {
            _at++;

            if (More && text[_at] == '?')
            {
                string rest = text[_at..];

                if (rest.StartsWith("?:", StringComparison.Ordinal))
                {
                    _at += 2;
                }
                else if (rest.StartsWith("?<", StringComparison.Ordinal) && !rest.StartsWith("?<=", StringComparison.Ordinal) && !rest.StartsWith("?<!", StringComparison.Ordinal))
                {
                    int close = text.IndexOf('>', _at);
                    _at = close < 0 ? throw Unsupported() : close + 1;
                }
                else
                {
                    throw Unsupported();
                }
            }

            Part inner = Alternatives();

            if (!More || text[_at] != ')')
            {
                throw Unsupported();
            }

            _at++;
            return inner;
        }

        private Part Escape()
        {
            int start = _at;
            char next = _at + 1 < text.Length ? text[_at + 1] : throw Unsupported();

            if (next is 'b' or 'B')
            {
                _at += 2;
                return new Nothing();
            }

            if (next is (>= '1' and <= '9') or 'k')
            {
                throw Unsupported();
            }

            int codePoint = ClassAtom();
            return codePoint >= 0 ? Literal(codePoint) : Set(text[start.._at], []);
        }

        private Characters Class()
        {
            int start = _at++;
            bool plain = true;
            List<int> listed = [];

            if (More && text[_at] == '^')
            {
                plain = false;
                _at++;
            }

            while (More && text[_at] != ']')
            {
                int low = ClassAtom();
                int high = low;

                if (_at + 1 < text.Length && text[_at] == '-' && text[_at + 1] != ']')
                {
                    _at++;
                    high = ClassAtom();
                }

                if (low < 0 || high < 0)
                {
                    plain = false;
                    listed.AddRange(((int[])[low, high]).Where(end => end >= 0));
                }
                else
                {
                    listed.AddRange(Enumerable.Range(low, Math.Min(high - low + 1, 64)).Where(member => !IsSurrogate(member)));
                }
            }

            if (!More)
            {
                throw Unsupported();
            }

            _at++;

            if (plain)
            {
                return listed.Count == 0 ? throw Unsupported() : new Characters([.. listed]);
            }

            return Set(text[start.._at], listed);
        }

        /// <summary>Reads one character. Gives -1 for an escape that stands for a set, such as <c>\d</c>.</summary>
        private int ClassAtom()
        {
            if (text[_at] != '\\')
            {
                return NextCodePoint();
            }

            _at++;

            if (!More)
            {
                throw Unsupported();
            }

            char escape = text[_at++];

            switch (escape)
            {
                case 'd' or 'D' or 'w' or 'W' or 's' or 'S':
                    return -1;

                case 'p' or 'P':
                    int close = text.IndexOf('}', _at);
                    _at = close < 0 ? throw Unsupported() : close + 1;
                    return -1;

                case 'n':
                    return '\n';

                case 'r':
                    return '\r';

                case 't':
                    return '\t';

                case 'f':
                    return '\f';

                case 'v':
                    return '\v';

                case 'b':
                    return '\b';

                case '0':
                    return '\0';

                case 'c':
                    return More ? text[_at++] % 32 : throw Unsupported();

                case 'x':
                    return Hex(2);

                case 'u':
                    return Unicode();

                default:
                    _at--;
                    return NextCodePoint();
            }
        }

        private int Unicode()
        {
            if (More && text[_at] == '{')
            {
                int close = text.IndexOf('}', _at);

                if (close < 0)
                {
                    throw Unsupported();
                }

                _at++;
                int value = Hex(close - _at);
                _at++;
                return value;
            }

            int first = Hex(4);

            if (first is >= 0xD800 and <= 0xDBFF && string.CompareOrdinal(text, _at, "\\u", 0, 2) == 0)
            {
                int position = _at;
                _at += 2;
                int second = Hex(4);

                if (second is >= 0xDC00 and <= 0xDFFF)
                {
                    return char.ConvertToUtf32((char)first, (char)second);
                }

                _at = position;
            }

            return first;
        }

        private int Hex(int digits)
        {
            if (_at + digits > text.Length
                || !int.TryParse(text.AsSpan(_at, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int value))
            {
                throw Unsupported();
            }

            _at += digits;
            return value;
        }
    }
}
