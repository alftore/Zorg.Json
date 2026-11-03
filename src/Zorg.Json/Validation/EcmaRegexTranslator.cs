using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Zorg.Json.Validation;

/// <summary>
/// Translates an ECMA-262 pattern in Unicode mode to .NET syntax with the same meaning. .NET matches UTF-16 units and
/// ECMA matches code points, so every translated atom consumes a whole code point. Surrogates only match in pairs: the
/// validated text is well-formed UTF-16, which means a lone surrogate in a pattern is valid but never matches.
/// </summary>
internal static class EcmaRegexTranslator
{
    private const int MaxBmp = 0xFFFF;
    private const int MinAstral = 0x10000;
    private const int MaxCodePoint = 0x10FFFF;

    // Surrogates are left out so that a class can never match half a pair.
    private const uint AllCategories = ((1u << 30) - 1) & ~(1u << (int)UnicodeCategory.Surrogate);

    private const string NeverMatches = @"[^\s\S]";
    private const string NotInsidePair = @"(?![\uDC00-\uDFFF])";
    private const string WordBoundary = "(?:(?<=[0-9A-Z_a-z])(?![0-9A-Z_a-z])|(?<![0-9A-Z_a-z])(?=[0-9A-Z_a-z]))";
    private const string NotWordBoundary = "(?:(?<=[0-9A-Z_a-z])(?=[0-9A-Z_a-z])|(?<![0-9A-Z_a-z])(?![0-9A-Z_a-z]))";

    // Indexed by UnicodeCategory.
    private static readonly string[] CategoryCodes =
    [
        "Lu", "Ll", "Lt", "Lm", "Lo", "Mn", "Mc", "Me", "Nd", "Nl", "No", "Zs", "Zl", "Zp", "Cc",
        "Cf", "Cs", "Co", "Pc", "Pd", "Ps", "Pe", "Pi", "Pf", "Po", "Sm", "Sc", "Sk", "So", "Cn",
    ];

    private static readonly CharacterSet Digit = new((0x30, 0x39));
    private static readonly CharacterSet Word = new((0x30, 0x39), (0x41, 0x5A), (0x5F, 0x5F), (0x61, 0x7A));
    private static readonly CharacterSet LineTerminator = new((0x0A, 0x0A), (0x0D, 0x0D), (0x2028, 0x2029));
    private static readonly CharacterSet WhiteSpace = new(
        (0x09, 0x0D), (0x20, 0x20), (0xA0, 0xA0), (0x1680, 0x1680), (0x2000, 0x200A), (0x2028, 0x2029),
        (0x202F, 0x202F), (0x205F, 0x205F), (0x3000, 0x3000), (0xFEFF, 0xFEFF));

    // .NET categories stop at the BMP, so the astral part of a \p{...} has to be spelled out as surrogate pairs.
    private static readonly Lazy<List<CodePointRange>[]> AstralCategories = new(ScanAstralCategories);

    public static string Translate(string pattern) => Translate(pattern, out _);

    /// <param name="pattern">The ECMA-262 pattern.</param>
    /// <param name="maxMinterms">An upper bound on how many sets of equivalent characters the result distinguishes.</param>
    public static string Translate(string pattern, out long maxMinterms)
    {
        Parser parser = new(pattern);
        string translated = parser.Translate();
        maxMinterms = parser.Partition.MaxMinterms;
        return translated;
    }

    private static List<CodePointRange>[] ScanAstralCategories()
    {
        List<CodePointRange>[] categories = new List<CodePointRange>[CategoryCodes.Length];

        for (int i = 0; i < categories.Length; i++)
        {
            categories[i] = [];
        }

        int start = MinAstral;
        UnicodeCategory current = CharUnicodeInfo.GetUnicodeCategory(start);

        for (int codePoint = MinAstral + 1; codePoint <= MaxCodePoint; codePoint++)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(codePoint);

            if (category != current)
            {
                categories[(int)current].Add(new CodePointRange(start, codePoint - 1));
                start = codePoint;
                current = category;
            }
        }

        categories[(int)current].Add(new CodePointRange(start, MaxCodePoint));
        return categories;
    }

    private static List<CodePointRange> Normalize(IEnumerable<CodePointRange> ranges)
    {
        List<CodePointRange> merged = [];

        foreach (CodePointRange range in ranges.OrderBy(range => range.Start))
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End + 1)
            {
                merged[^1] = merged[^1] with { End = Math.Max(merged[^1].End, range.End) };
            }
            else
            {
                merged.Add(range);
            }
        }

        return merged;
    }

    /// <summary>The part of <paramref name="start"/> to <paramref name="end"/> not covered by normalized ranges.</summary>
    private static List<CodePointRange> Complement(List<CodePointRange> ranges, int start, int end)
    {
        List<CodePointRange> gaps = [];
        int next = start;

        foreach (CodePointRange range in ranges)
        {
            if (range.Start > next)
            {
                gaps.Add(new CodePointRange(next, range.Start - 1));
            }

            next = range.End + 1;
        }

        if (next <= end)
        {
            gaps.Add(new CodePointRange(next, end));
        }

        return gaps;
    }

    private static string Render(CharacterSet set, bool negated, CharacterPartition partition)
    {
        List<CodePointRange> ranges = Normalize(set.Ranges.Concat(AstralRangesOf(set.Categories)));
        List<CodePointRange> astral =
            [.. ranges.Where(range => range.End >= MinAstral).Select(range => range with { Start = Math.Max(range.Start, MinAstral) })];

        StringBuilder bmp = new();
        AppendCategories(bmp, set.Categories, partition);

        foreach (CodePointRange range in ranges.Where(range => range.Start <= MaxBmp))
        {
            AppendClassRange(bmp, range.Start, Math.Min(range.End, MaxBmp), partition);
        }

        List<string> alternatives = [];

        if (negated)
        {
            alternatives.Add($@"[^{bmp}\uD800-\uDFFF]");
            partition.Add(0xD800, 0xDFFF);
            astral = Complement(astral, MinAstral, MaxCodePoint);
        }
        else if (bmp.Length > 0)
        {
            alternatives.Add($"[{bmp}]");
        }

        if (astral.Count == 0)
        {
            return alternatives.Count == 0 ? NeverMatches : alternatives[0];
        }

        AddSurrogatePairs(alternatives, astral, partition);
        return $"(?:{string.Join('|', alternatives)})";
    }

    private static IEnumerable<CodePointRange> AstralRangesOf(uint categories)
    {
        if (categories == 0)
        {
            return [];
        }

        List<CodePointRange>[] astral = AstralCategories.Value;
        return Enumerable.Range(0, astral.Length).Where(i => (categories & (1u << i)) != 0).SelectMany(i => astral[i]);
    }

    private static void AppendCategories(StringBuilder output, uint categories, CharacterPartition partition)
    {
        // C is missing here because it includes surrogates.
        foreach (char group in "LMNZPS")
        {
            uint mask = CategoryMask(group.ToString());

            if ((categories & mask) == mask)
            {
                output.Append(@"\p{").Append(group).Append('}');
                partition.AddCategory(group.ToString());
                categories &= ~mask;
            }
        }

        for (int i = 0; i < CategoryCodes.Length; i++)
        {
            if ((categories & (1u << i)) != 0)
            {
                output.Append(@"\p{").Append(CategoryCodes[i]).Append('}');
                partition.AddCategory(CategoryCodes[i]);
            }
        }
    }

    private static void AppendClassRange(StringBuilder output, int start, int end, CharacterPartition partition)
    {
        partition.Add(start, end);
        AppendClassCharacter(output, start);

        if (end > start)
        {
            output.Append('-');
            AppendClassCharacter(output, end);
        }
    }

    private static void AppendClassCharacter(StringBuilder output, int character)
    {
        if (char.IsAsciiLetterOrDigit((char)character) || character == '_')
        {
            output.Append((char)character);
        }
        else
        {
            AppendUnicodeEscape(output, character);
        }
    }

    private static void AppendUnicodeEscape(StringBuilder output, int character) =>
        output.Append(@"\u").Append(character.ToString("X4", CultureInfo.InvariantCulture));

    /// <summary>Spells out astral ranges, sharing one alternative between all the ranges under a high surrogate.</summary>
    private static void AddSurrogatePairs(List<string> alternatives, List<CodePointRange> astral, CharacterPartition partition)
    {
        StringBuilder low = new();
        int openHigh = -1;

        void Flush()
        {
            if (low.Length > 0)
            {
                StringBuilder alternative = new();
                AppendUnicodeEscape(alternative, openHigh);
                partition.Add(openHigh, openHigh);
                alternatives.Add(alternative.Append('[').Append(low).Append(']').ToString());
                low.Clear();
            }
        }

        foreach (CodePointRange range in astral)
        {
            int codePoint = range.Start;

            while (codePoint <= range.End)
            {
                int blockEnd = codePoint | 0x3FF;

                if (LowSurrogate(codePoint) == 0xDC00 && range.End >= blockEnd)
                {
                    int lastFullBlock = ((range.End + 1) & ~0x3FF) - 1;
                    Flush();

                    StringBuilder alternative = new("[");
                    AppendClassRange(alternative, HighSurrogate(codePoint), HighSurrogate(lastFullBlock), partition);
                    partition.Add(0xDC00, 0xDFFF);
                    alternatives.Add(alternative.Append(@"][\uDC00-\uDFFF]").ToString());
                    codePoint = lastFullBlock + 1;
                    continue;
                }

                if (HighSurrogate(codePoint) != openHigh)
                {
                    Flush();
                    openHigh = HighSurrogate(codePoint);
                }

                int end = Math.Min(range.End, blockEnd);
                AppendClassRange(low, LowSurrogate(codePoint), LowSurrogate(end), partition);
                codePoint = end + 1;
            }
        }

        Flush();
    }

    private static int HighSurrogate(int codePoint) => 0xD800 + ((codePoint - MinAstral) >> 10);

    private static int LowSurrogate(int codePoint) => 0xDC00 + ((codePoint - MinAstral) & 0x3FF);

    private static bool IsSurrogate(int codePoint) => codePoint is >= 0xD800 and <= 0xDFFF;

    private static uint CategoryMask(string code)
    {
        if (code == "LC")
        {
            return CategoryMask("Lu") | CategoryMask("Ll") | CategoryMask("Lt");
        }

        uint mask = 0;

        for (int i = 0; i < CategoryCodes.Length; i++)
        {
            if (CategoryCodes[i].StartsWith(code, StringComparison.Ordinal))
            {
                mask |= 1u << i;
            }
        }

        return mask & AllCategories;
    }

    private static string? CategoryCode(string name) => name switch
    {
        "L" or "Letter" => "L",
        "LC" or "Cased_Letter" => "LC",
        "Lu" or "Uppercase_Letter" => "Lu",
        "Ll" or "Lowercase_Letter" => "Ll",
        "Lt" or "Titlecase_Letter" => "Lt",
        "Lm" or "Modifier_Letter" => "Lm",
        "Lo" or "Other_Letter" => "Lo",
        "M" or "Mark" or "Combining_Mark" => "M",
        "Mn" or "Nonspacing_Mark" => "Mn",
        "Mc" or "Spacing_Mark" => "Mc",
        "Me" or "Enclosing_Mark" => "Me",
        "N" or "Number" => "N",
        "Nd" or "Decimal_Number" or "digit" => "Nd",
        "Nl" or "Letter_Number" => "Nl",
        "No" or "Other_Number" => "No",
        "P" or "Punctuation" or "punct" => "P",
        "Pc" or "Connector_Punctuation" => "Pc",
        "Pd" or "Dash_Punctuation" => "Pd",
        "Ps" or "Open_Punctuation" => "Ps",
        "Pe" or "Close_Punctuation" => "Pe",
        "Pi" or "Initial_Punctuation" => "Pi",
        "Pf" or "Final_Punctuation" => "Pf",
        "Po" or "Other_Punctuation" => "Po",
        "S" or "Symbol" => "S",
        "Sm" or "Math_Symbol" => "Sm",
        "Sc" or "Currency_Symbol" => "Sc",
        "Sk" or "Modifier_Symbol" => "Sk",
        "So" or "Other_Symbol" => "So",
        "Z" or "Separator" => "Z",
        "Zs" or "Space_Separator" => "Zs",
        "Zl" or "Line_Separator" => "Zl",
        "Zp" or "Paragraph_Separator" => "Zp",
        "C" or "Other" => "C",
        "Cc" or "Control" or "cntrl" => "Cc",
        "Cf" or "Format" => "Cf",
        "Cs" or "Surrogate" => "Cs",
        "Co" or "Private_Use" => "Co",
        "Cn" or "Unassigned" => "Cn",
        _ => null,
    };

    private readonly record struct CodePointRange(int Start, int End);

    private readonly record struct Backreference(int Position, int Number, string? Name);

    /// <summary>A union of general categories and code point ranges. It never contains a surrogate.</summary>
    private sealed class CharacterSet
    {
        public CharacterSet(params ReadOnlySpan<(int Start, int End)> ranges)
        {
            foreach ((int start, int end) in ranges)
            {
                Add(start, end);
            }
        }

        public uint Categories { get; set; }

        public List<CodePointRange> Ranges { get; } = [];

        public void Add(int start, int end)
        {
            if (start < 0xD800)
            {
                Ranges.Add(new CodePointRange(start, Math.Min(end, 0xD7FF)));
            }

            if (end > 0xDFFF)
            {
                Ranges.Add(new CodePointRange(Math.Max(start, 0xE000), end));
            }
        }

        public void Add(CharacterSet other, bool negated)
        {
            if (!negated)
            {
                Categories |= other.Categories;
                Ranges.AddRange(other.Ranges);
            }
            else if (other.Categories != 0)
            {
                Categories |= AllCategories & ~other.Categories;
            }
            else
            {
                foreach (CodePointRange range in Complement(Normalize(other.Ranges), 0, MaxCodePoint))
                {
                    Add(range.Start, range.End);
                }
            }
        }
    }

    /// <summary>
    /// Tracks what the pattern tells apart. Ranges cut the UTF-16 units into intervals, and categories, which are
    /// disjoint or nested, cut each interval further.
    /// </summary>
    private sealed class CharacterPartition
    {
        private readonly HashSet<int> _boundaries = [];
        private readonly HashSet<string> _categories = new(StringComparer.Ordinal);

        public long MaxMinterms => (_boundaries.Count + 1L) * (_categories.Count + 1L);

        public void Add(int start, int end)
        {
            _boundaries.Add(start);
            _boundaries.Add(end + 1);
        }

        public void AddCategory(string code) => _categories.Add(code);
    }

    /// <summary>A group being parsed, and the names captured by its finished and current alternatives.</summary>
    private sealed class Frame(bool isLookaround, string? name)
    {
        public bool IsLookaround { get; } = isLookaround;

        public string? Name { get; } = name;

        public HashSet<string> Finished { get; } = [];

        public HashSet<string> Current { get; } = [];
    }

    /// <summary>
    /// Scans the pattern once without recursion, so that deep nesting can't overflow the stack. An unclosed parenthesis
    /// is copied through for the .NET regex parser to reject.
    /// </summary>
    private sealed class Parser(string pattern)
    {
        private readonly string _pattern = pattern;
        private readonly StringBuilder _output = new(pattern.Length);
        private readonly Stack<Frame> _frames = new([new Frame(false, null)]);
        private readonly List<string?> _groups = [];
        private readonly Dictionary<string, string> _groupNames = new(StringComparer.Ordinal);
        private readonly List<Backreference> _backreferences = [];
        private int _position;
        private bool _quantifiable;
        private bool _hasZeroWidthTest;

        public CharacterPartition Partition { get; } = new();

        private int Current => _position < _pattern.Length ? _pattern[_position] : -1;

        public string Translate()
        {
            while (_position < _pattern.Length)
            {
                switch (_pattern[_position])
                {
                    case '\\':
                        ParseEscape();
                        break;
                    case '[':
                        ParseClass();
                        break;
                    case '(':
                        OpenGroup();
                        break;
                    case ')':
                        CloseGroup();
                        break;
                    case '|':
                        _position++;
                        NextAlternative();
                        break;
                    case '^':
                        _position++;
                        AppendAssertion("^");
                        break;
                    case '$':
                        _position++;
                        AppendAssertion(@"\z");
                        break;
                    case '.':
                        _position++;
                        AppendSet(LineTerminator, negated: true);
                        break;
                    case '*' or '+' or '?' or '{':
                        ParseQuantifier();
                        break;
                    case '}' or ']':
                        throw Error($"lone '{_pattern[_position]}' must be escaped");
                    default:
                        AppendLiteral(ReadCodePoint());
                        break;
                }
            }

            if (_frames.Count > 1)
            {
                return _output.ToString();
            }

            ResolveBackreferences();

            // .NET tries a match at every UTF-16 unit and ECMA at every code point. Consuming atoms can't start inside a
            // pair, but a pattern that only tests its surroundings can succeed there.
            return _hasZeroWidthTest ? $"{NotInsidePair}(?:{_output})" : _output.ToString();
        }

        private ArgumentException Error(string message) =>
            new($"Invalid ECMA-262 pattern '{_pattern}' at position {_position}: {message}.");

        private int ReadCodePoint()
        {
            char character = _pattern[_position++];

            if (char.IsHighSurrogate(character) && _position < _pattern.Length && char.IsLowSurrogate(_pattern[_position]))
            {
                return char.ConvertToUtf32(character, _pattern[_position++]);
            }

            return character;
        }

        private void AppendAssertion(string text)
        {
            _output.Append(text);
            _quantifiable = false;
        }

        private void AppendSet(CharacterSet set, bool negated)
        {
            _output.Append(Render(set, negated, Partition));
            _quantifiable = true;
        }

        private void AppendLiteral(int codePoint)
        {
            if (IsSurrogate(codePoint))
            {
                _output.Append(NeverMatches);
            }
            else if (codePoint >= MinAstral)
            {
                _output.Append("(?:");
                AppendUnicodeEscape(_output, HighSurrogate(codePoint));
                AppendUnicodeEscape(_output, LowSurrogate(codePoint));
                _output.Append(')');
                Partition.Add(HighSurrogate(codePoint), HighSurrogate(codePoint));
                Partition.Add(LowSurrogate(codePoint), LowSurrogate(codePoint));
            }
            else if (codePoint is < 0x20 or 0x7F)
            {
                AppendUnicodeEscape(_output, codePoint);
                Partition.Add(codePoint, codePoint);
            }
            else
            {
                if (codePoint is '\\' or '*' or '+' or '?' or '|' or '{' or '}' or '[' or ']' or '(' or ')' or '^' or '$' or '.')
                {
                    _output.Append('\\');
                }

                _output.Append((char)codePoint);
                Partition.Add(codePoint, codePoint);
            }

            _quantifiable = true;
        }

        private void NextAlternative()
        {
            Frame frame = _frames.Peek();
            frame.Finished.UnionWith(frame.Current);
            frame.Current.Clear();
            _output.Append('|');
            _quantifiable = false;
        }

        private void OpenGroup()
        {
            _position++;
            string prefix = "(";
            bool isLookaround = false;
            string? name = null;

            if (Current == '?')
            {
                _position++;

                if (TryConsume("=") || TryConsume("!"))
                {
                    prefix = $"(?{_pattern[_position - 1]}";
                    isLookaround = true;
                    _hasZeroWidthTest = true;
                }
                else if (TryConsume("<=") || TryConsume("<!"))
                {
                    prefix = $"(?<{_pattern[_position - 1]}";
                    isLookaround = true;
                    _hasZeroWidthTest = true;
                }
                else if (TryConsume(":"))
                {
                    prefix = "(?:";
                }
                else if (TryConsume("<"))
                {
                    name = ReadGroupName();
                    prefix = $"(?<{DeclareGroupName(name)}>";
                }
                else
                {
                    throw Error("unsupported group syntax after '(?'");
                }
            }

            if (prefix.Length == 1 || name is not null)
            {
                _groups.Add(name);
            }

            _frames.Push(new Frame(isLookaround, name));
            _output.Append(prefix);
            _quantifiable = false;
        }

        private void CloseGroup()
        {
            if (_frames.Count == 1)
            {
                throw Error("unmatched ')'");
            }

            _position++;
            _output.Append(')');
            Frame frame = _frames.Pop();
            frame.Current.UnionWith(frame.Finished);

            if (frame.Name is not null && !frame.Current.Add(frame.Name))
            {
                throw Error($"duplicate group name '{frame.Name}'");
            }

            Frame parent = _frames.Peek();

            if (parent.Current.Overlaps(frame.Current))
            {
                throw Error($"duplicate group name '{parent.Current.Intersect(frame.Current).First()}'");
            }

            parent.Current.UnionWith(frame.Current);
            _quantifiable = !frame.IsLookaround;
        }

        private bool TryConsume(string text)
        {
            if (string.CompareOrdinal(_pattern, _position, text, 0, text.Length) != 0)
            {
                return false;
            }

            _position += text.Length;
            return true;
        }

        private string ReadGroupName()
        {
            StringBuilder name = new();

            while (Current != '>')
            {
                if (Current == -1)
                {
                    throw Error("unterminated group name");
                }

                int codePoint = ReadGroupNameCodePoint();

                if (!IsIdentifierPart(codePoint, isStart: name.Length == 0))
                {
                    throw Error("invalid group name");
                }

                name.Append(char.ConvertFromUtf32(codePoint));
            }

            if (name.Length == 0)
            {
                throw Error("empty group name");
            }

            _position++;
            return name.ToString();
        }

        private int ReadGroupNameCodePoint()
        {
            if (Current != '\\')
            {
                return ReadCodePoint();
            }

            _position++;

            if (Current != 'u')
            {
                throw Error("invalid group name");
            }

            return ParseUnicodeEscape();
        }

        private static bool IsIdentifierPart(int codePoint, bool isStart)
        {
            if (codePoint is '$' or '_')
            {
                return true;
            }

            if (IsSurrogate(codePoint))
            {
                return false;
            }

            return CharUnicodeInfo.GetUnicodeCategory(codePoint) switch
            {
                UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                    or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber => true,
                UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.DecimalDigitNumber
                    or UnicodeCategory.ConnectorPunctuation => !isStart,
                _ => !isStart && codePoint is 0x200C or 0x200D,
            };
        }

        /// <summary>.NET group names are narrower than ECMA identifiers, so a name with a '$' or an astral letter is replaced.</summary>
        private string DeclareGroupName(string name)
        {
            if (!_groupNames.TryGetValue(name, out string? translated))
            {
                translated = name.All(character => char.IsLetterOrDigit(character) || character == '_')
                    ? name
                    : string.Create(CultureInfo.InvariantCulture, $"group_{_groups.Count + 1}");
                _groupNames.Add(name, translated);
            }

            return translated;
        }

        private void ParseEscape()
        {
            _position++;

            switch (Current)
            {
                case -1:
                    throw Error(@"'\' at end of pattern");
                case 'b':
                    _position++;
                    _hasZeroWidthTest = true;
                    AppendAssertion(WordBoundary);
                    break;
                case 'B':
                    _position++;
                    _hasZeroWidthTest = true;
                    AppendAssertion(NotWordBoundary);
                    break;
                case 'k':
                    ParseNamedBackreference();
                    break;
                case >= '1' and <= '9':
                    ParseNumberedBackreference();
                    break;
                default:
                    if (TryParseSetEscape(out CharacterSet? set, out bool negated))
                    {
                        AppendSet(set, negated);
                    }
                    else
                    {
                        AppendLiteral(ParseCharacterEscape(inClass: false));
                    }

                    break;
            }
        }

        private void ParseNamedBackreference()
        {
            _position++;

            if (Current != '<')
            {
                throw Error(@"'\k' must be followed by a group name");
            }

            _position++;
            _backreferences.Add(new Backreference(_output.Length, 0, ReadGroupName()));
            _quantifiable = true;
        }

        private void ParseNumberedBackreference()
        {
            int start = _position;

            while (char.IsAsciiDigit((char)Current))
            {
                _position++;
            }

            if (!int.TryParse(_pattern.AsSpan(start, _position - start), NumberStyles.None, CultureInfo.InvariantCulture, out int number))
            {
                number = int.MaxValue;
            }

            _backreferences.Add(new Backreference(_output.Length, number, null));
            _quantifiable = true;
        }

        /// <summary>
        /// Runs at the end because a reference can precede its group. The conditional is there because ECMA lets a
        /// reference to a group that hasn't matched succeed on the empty string, and .NET fails it.
        /// </summary>
        private void ResolveBackreferences()
        {
            for (int i = _backreferences.Count - 1; i >= 0; i--)
            {
                Backreference reference = _backreferences[i];
                string group = reference.Name is null ? GroupByNumber(reference.Number) : GroupByName(reference.Name);
                _output.Insert(reference.Position, $@"(?({group})\k<{group}>)");
            }
        }

        private string GroupByName(string name) =>
            _groupNames.TryGetValue(name, out string? translated) ? translated : throw Error($"no group named '{name}'");

        // .NET numbers unnamed groups first and named groups after them, so numbers don't line up with ECMA's.
        private string GroupByNumber(int number)
        {
            if (number > _groups.Count)
            {
                throw Error($@"'\{number}' refers to a group that doesn't exist");
            }

            return _groups[number - 1] is { } name
                ? _groupNames[name]
                : _groups.Take(number).Count(group => group is null).ToString(CultureInfo.InvariantCulture);
        }

        private bool TryParseSetEscape([NotNullWhen(true)] out CharacterSet? set, out bool negated)
        {
            int escape = Current;
            negated = escape is 'D' or 'W' or 'S' or 'P';

            set = escape switch
            {
                'd' or 'D' => Digit,
                'w' or 'W' => Word,
                's' or 'S' => WhiteSpace,
                _ => null,
            };

            if (set is not null)
            {
                _position++;
                return true;
            }

            if (escape is 'p' or 'P')
            {
                _position++;
                set = ParseProperty();
                return true;
            }

            return false;
        }

        private CharacterSet ParseProperty()
        {
            if (Current != '{')
            {
                throw Error(@"'\p' must be followed by a property name in braces");
            }

            int end = _pattern.IndexOf('}', _position);

            if (end < 0)
            {
                throw Error("unterminated property name");
            }

            CharacterSet set = LookUpProperty(_pattern[(_position + 1)..end]);
            _position = end + 1;
            return set;
        }

        private CharacterSet LookUpProperty(string property)
        {
            int separator = property.IndexOf('=', StringComparison.Ordinal);

            if (separator >= 0)
            {
                string name = property[..separator];

                if (name is "Script" or "sc" or "Script_Extensions" or "scx")
                {
                    throw Error($@"'\p{{{property}}}' is not supported because .NET has no Unicode script data");
                }

                return name is "General_Category" or "gc" && CategoryCode(property[(separator + 1)..]) is { } value
                    ? new CharacterSet { Categories = CategoryMask(value) }
                    : throw Error($@"unsupported Unicode property '\p{{{property}}}'");
            }

            return property switch
            {
                "Any" => new CharacterSet((0, MaxCodePoint)),
                "ASCII" => new CharacterSet((0, 0x7F)),
                "Assigned" => new CharacterSet { Categories = AllCategories & ~CategoryMask("Cn") },
                _ when CategoryCode(property) is { } code => new CharacterSet { Categories = CategoryMask(code) },
                _ => throw Error($@"unsupported Unicode property '\p{{{property}}}'"),
            };
        }

        private int ParseCharacterEscape(bool inClass)
        {
            int escape = Current;

            switch (escape)
            {
                case 't':
                    _position++;
                    return '\t';
                case 'n':
                    _position++;
                    return '\n';
                case 'v':
                    _position++;
                    return '\v';
                case 'f':
                    _position++;
                    return '\f';
                case 'r':
                    _position++;
                    return '\r';
                case '0':
                    _position++;

                    if (char.IsAsciiDigit((char)Current))
                    {
                        throw Error("octal escapes are not allowed");
                    }

                    return 0;
                case 'c':
                    _position++;

                    if (!char.IsAsciiLetter((char)Current))
                    {
                        throw Error(@"'\c' must be followed by an ASCII letter");
                    }

                    return _pattern[_position++] % 32;
                case 'x':
                    _position++;
                    return ReadHex(2);
                case 'u':
                    return ParseUnicodeEscape();
                case 'b' when inClass:
                    _position++;
                    return '\b';
                case '-' when inClass:
                case '^' or '$' or '\\' or '.' or '*' or '+' or '?' or '(' or ')' or '[' or ']' or '{' or '}' or '|' or '/':
                    _position++;
                    return escape;
                default:
                    throw Error($@"invalid escape '\{_pattern[_position]}'");
            }
        }

        private int ReadHex(int length)
        {
            if (_position + length > _pattern.Length
                || !int.TryParse(_pattern.AsSpan(_position, length), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int value))
            {
                throw Error("invalid hexadecimal escape");
            }

            _position += length;
            return value;
        }

        private int ParseUnicodeEscape()
        {
            _position++;

            if (Current == '{')
            {
                return ParseBracedCodePoint();
            }

            int unit = ReadHex(4);

            if (char.IsHighSurrogate((char)unit) && string.CompareOrdinal(_pattern, _position, @"\u", 0, 2) == 0)
            {
                int resume = _position;
                _position += 2;

                if (Current != '{')
                {
                    int next = ReadHex(4);

                    if (char.IsLowSurrogate((char)next))
                    {
                        return char.ConvertToUtf32((char)unit, (char)next);
                    }
                }

                _position = resume;
            }

            return unit;
        }

        private int ParseBracedCodePoint()
        {
            int end = _pattern.IndexOf('}', _position);

            if (end <= _position + 1
                || !BigInteger.TryParse("0" + _pattern[(_position + 1)..end], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out BigInteger value)
                || value > MaxCodePoint)
            {
                throw Error(@"invalid '\u{...}' escape");
            }

            _position = end + 1;
            return (int)value;
        }

        private void ParseClass()
        {
            _position++;
            bool negated = Current == '^';

            if (negated)
            {
                _position++;
            }

            CharacterSet set = new();

            while (Current != ']')
            {
                if (Current == -1)
                {
                    throw Error("unterminated character class");
                }

                int? start = ParseClassAtom(set);

                if (Current == '-' && _position + 1 < _pattern.Length && _pattern[_position + 1] != ']')
                {
                    _position++;
                    int? end = ParseClassAtom(set);

                    if (start is null || end is null)
                    {
                        throw Error("a character class escape can't be a range endpoint");
                    }

                    if (start > end)
                    {
                        throw Error("range out of order in character class");
                    }

                    set.Add(start.Value, end.Value);
                }
                else if (start is { } single)
                {
                    set.Add(single, single);
                }
            }

            _position++;
            AppendSet(set, negated);
        }

        /// <summary>Returns the code point, or null after adding a multi-character escape such as \d to the set.</summary>
        private int? ParseClassAtom(CharacterSet set)
        {
            if (Current != '\\')
            {
                return ReadCodePoint();
            }

            _position++;

            if (Current == -1)
            {
                throw Error(@"'\' at end of pattern");
            }

            if (TryParseSetEscape(out CharacterSet? escape, out bool negated))
            {
                set.Add(escape, negated);
                return null;
            }

            return ParseCharacterEscape(inClass: true);
        }

        private void ParseQuantifier()
        {
            string quantifier = _pattern[_position] == '{' ? ParseBracedQuantifier() : _pattern[_position++].ToString();

            if (!_quantifiable)
            {
                throw Error($"nothing to repeat before '{quantifier}'");
            }

            _output.Append(quantifier);

            if (Current == '?')
            {
                _position++;
                _output.Append('?');
            }

            _quantifiable = false;
        }

        private string ParseBracedQuantifier()
        {
            int position = _position + 1;
            string? minimum = ReadDigits(ref position);
            string? maximum = minimum;
            bool hasComma = position < _pattern.Length && _pattern[position] == ',';

            if (hasComma)
            {
                position++;
                maximum = ReadDigits(ref position);
            }

            if (minimum is null || position >= _pattern.Length || _pattern[position] != '}')
            {
                throw Error("lone '{' must be escaped");
            }

            BigInteger low = BigInteger.Parse(minimum, CultureInfo.InvariantCulture);

            if (maximum is null)
            {
                _position = position + 1;
                return $"{{{Clamp(low)},}}";
            }

            BigInteger high = BigInteger.Parse(maximum, CultureInfo.InvariantCulture);

            if (low > high)
            {
                throw Error("numbers out of order in quantifier");
            }

            _position = position + 1;
            return hasComma ? $"{{{Clamp(low)},{Clamp(high)}}}" : $"{{{Clamp(low)}}}";
        }

        private string? ReadDigits(ref int position)
        {
            int start = position;

            while (position < _pattern.Length && char.IsAsciiDigit(_pattern[position]))
            {
                position++;
            }

            return position == start ? null : _pattern[start..position];
        }

        // ECMA counts have no limit, and .NET's stop at int.MaxValue. No string is long enough to tell them apart.
        private static string Clamp(BigInteger count) =>
            BigInteger.Min(count, int.MaxValue).ToString(CultureInfo.InvariantCulture);
    }
}
