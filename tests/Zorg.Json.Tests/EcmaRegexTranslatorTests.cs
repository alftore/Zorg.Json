using System;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Zorg.Json.Validation;

namespace Zorg.Json.Tests;

public sealed class EcmaRegexTranslatorTests
{
    private const string Grin = "\U0001F600";
    private const string Grin2 = "\U0001F601";
    private const string ScriptA = "\U0001D49C"; // MATHEMATICAL SCRIPT CAPITAL A, an astral Lu
    private const string AstralDigit = "\U0001D7D8"; // MATHEMATICAL DOUBLE-STRUCK DIGIT ZERO, an astral Nd

    private static bool Matches(string pattern, string input) => PatternCompiler.Compile(pattern).IsMatch(input);

    private static bool UsesLinearEngine(string pattern) =>
        PatternCompiler.Compile(pattern).Options.HasFlag(RegexOptions.NonBacktracking);

    [Theory]
    [InlineData("abc", "abc")]
    [InlineData(@"\d", "[0-9]")]
    [InlineData(@"\w", "[0-9A-Z_a-z]")]
    [InlineData("^a$", @"^a\z")]
    [InlineData("[a-c]", "[a-c]")]
    [InlineData("[]", @"[^\s\S]")]
    [InlineData(@"\p{Cc}", @"[\p{Cc}]")]
    [InlineData(@"\/", "/")]
    [InlineData(@"a\.b", @"a\.b")]
    [InlineData(@"\u{1F600}", @"(?:\uD83D\uDE00)")]
    [InlineData("(a)|(?:b)", "(a)|(?:b)")]
    [InlineData("a{2,3}?", "a{2,3}?")]
    public void Translation_is_exact(string pattern, string expected) =>
        Assert.Equal(expected, EcmaRegexTranslator.Translate(pattern));

    [Theory]
    [InlineData("0", true)]
    [InlineData("9", true)]
    [InlineData("\u0660", false)] // ARABIC-INDIC DIGIT ZERO
    [InlineData("\u07C0", false)] // NKO DIGIT ZERO
    [InlineData("a", false)]
    public void Digit_is_ascii_only(string input, bool expected)
    {
        Assert.Equal(expected, Matches(@"^\d$", input));
        Assert.Equal(!expected, Matches(@"^\D$", input));
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("Z", true)]
    [InlineData("0", true)]
    [InlineData("_", true)]
    [InlineData("\u00E9", false)]
    [InlineData("\u017F", false)] // LATIN SMALL LETTER LONG S
    [InlineData("\u212A", false)] // KELVIN SIGN
    [InlineData("-", false)]
    public void Word_is_ascii_only(string input, bool expected)
    {
        Assert.Equal(expected, Matches(@"^\w$", input));
        Assert.Equal(!expected, Matches(@"^\W$", input));
    }

    [Theory]
    [InlineData("\t", true)]
    [InlineData("\n", true)]
    [InlineData("\v", true)]
    [InlineData("\f", true)]
    [InlineData("\r", true)]
    [InlineData(" ", true)]
    [InlineData("\u00A0", true)]
    [InlineData("\u1680", true)]
    [InlineData("\u2000", true)]
    [InlineData("\u200A", true)]
    [InlineData("\u2028", true)]
    [InlineData("\u2029", true)]
    [InlineData("\u202F", true)]
    [InlineData("\u205F", true)]
    [InlineData("\u3000", true)]
    [InlineData("\uFEFF", true)]
    [InlineData("\u0085", false)] // NEXT LINE is White_Space in Unicode but not in ECMA
    [InlineData("\u180E", false)]
    [InlineData("\u200B", false)]
    [InlineData("\u0001", false)]
    [InlineData("a", false)]
    public void White_space_is_the_ecma_set(string input, bool expected)
    {
        Assert.Equal(expected, Matches(@"^\s$", input));
        Assert.Equal(!expected, Matches(@"^\S$", input));
    }

    [Theory]
    [InlineData(@"\bfoo\b", "a foo b", true)]
    [InlineData(@"\bfoo\b", "foo", true)]
    [InlineData(@"\bfoo\b", "afoo", false)]
    [InlineData(@"\bfoo\b", "\u00E9foo", true)] // \u00E9 isn't a word character, so there's a boundary
    [InlineData(@"\Bfoo", "afoo", true)]
    [InlineData(@"\Bfoo", "\u00E9foo", false)]
    [InlineData(@"\Bfoo", "foo", false)]
    [InlineData(@"^\B$", "", true)]
    [InlineData(@"\b", "\u00E9", false)]
    public void Word_boundaries_use_ascii_words(string pattern, string input, bool expected) =>
        Assert.Equal(expected, Matches(pattern, input));

    [Theory]
    [InlineData("^abc$", "abc", true)]
    [InlineData("^abc$", "abc\n", false)]
    [InlineData("^abc$", "abc\r\n", false)]
    [InlineData("^a$", "b\na", false)]
    [InlineData("a$", "ba", true)]
    [InlineData("^b", "a\nb", false)]
    [InlineData("[$]", "$", true)]
    [InlineData("[$]", "a", false)]
    [InlineData(@"\$", "$", true)]
    public void Anchors_only_match_the_ends_of_the_input(string pattern, string input, bool expected) =>
        Assert.Equal(expected, Matches(pattern, input));

    [Theory]
    [InlineData("a", true)]
    [InlineData("\u0085", true)]
    [InlineData("\t", true)]
    [InlineData("\n", false)]
    [InlineData("\r", false)]
    [InlineData("\u2028", false)]
    [InlineData("\u2029", false)]
    [InlineData(Grin, true)]
    [InlineData("ab", false)]
    [InlineData("", false)]
    public void Dot_matches_one_code_point_except_line_terminators(string input, bool expected) =>
        Assert.Equal(expected, Matches("^.$", input));

    [Fact]
    public void Dot_in_a_class_is_literal()
    {
        Assert.True(Matches("^[.]$", "."));
        Assert.False(Matches("^[.]$", "a"));
    }

    [Theory]
    [InlineData(@"\p{Letter}", "L")]
    [InlineData(@"\p{L}", "L")]
    [InlineData(@"\p{Uppercase_Letter}", "Lu")]
    [InlineData(@"\p{Lowercase_Letter}", "Ll")]
    [InlineData(@"\p{Titlecase_Letter}", "Lt")]
    [InlineData(@"\p{Modifier_Letter}", "Lm")]
    [InlineData(@"\p{Other_Letter}", "Lo")]
    [InlineData(@"\p{Mark}", "M")]
    [InlineData(@"\p{Combining_Mark}", "M")]
    [InlineData(@"\p{Nonspacing_Mark}", "Mn")]
    [InlineData(@"\p{Spacing_Mark}", "Mc")]
    [InlineData(@"\p{Enclosing_Mark}", "Me")]
    [InlineData(@"\p{Number}", "N")]
    [InlineData(@"\p{Decimal_Number}", "Nd")]
    [InlineData(@"\p{digit}", "Nd")]
    [InlineData(@"\p{Letter_Number}", "Nl")]
    [InlineData(@"\p{Other_Number}", "No")]
    [InlineData(@"\p{Punctuation}", "P")]
    [InlineData(@"\p{punct}", "P")]
    [InlineData(@"\p{Connector_Punctuation}", "Pc")]
    [InlineData(@"\p{Dash_Punctuation}", "Pd")]
    [InlineData(@"\p{Open_Punctuation}", "Ps")]
    [InlineData(@"\p{Close_Punctuation}", "Pe")]
    [InlineData(@"\p{Initial_Punctuation}", "Pi")]
    [InlineData(@"\p{Final_Punctuation}", "Pf")]
    [InlineData(@"\p{Other_Punctuation}", "Po")]
    [InlineData(@"\p{Symbol}", "S")]
    [InlineData(@"\p{Math_Symbol}", "Sm")]
    [InlineData(@"\p{Currency_Symbol}", "Sc")]
    [InlineData(@"\p{Modifier_Symbol}", "Sk")]
    [InlineData(@"\p{Other_Symbol}", "So")]
    [InlineData(@"\p{Separator}", "Z")]
    [InlineData(@"\p{Space_Separator}", "Zs")]
    [InlineData(@"\p{Line_Separator}", "Zl")]
    [InlineData(@"\p{Paragraph_Separator}", "Zp")]
    [InlineData(@"\p{Control}", "Cc")]
    [InlineData(@"\p{cntrl}", "Cc")]
    [InlineData(@"\p{Format}", "Cf")]
    [InlineData(@"\p{Private_Use}", "Co")]
    [InlineData(@"\p{Unassigned}", "Cn")]
    [InlineData(@"\p{gc=Letter}", "L")]
    [InlineData(@"\p{gc=Lu}", "Lu")]
    [InlineData(@"\p{General_Category=Uppercase_Letter}", "Lu")]
    public void Property_names_map_to_dotnet_categories(string pattern, string category) =>
        Assert.Contains($@"\p{{{category}}}", EcmaRegexTranslator.Translate(pattern), StringComparison.Ordinal);

    [Theory]
    [InlineData(@"^\p{Letter}$", "a", true)]
    [InlineData(@"^\p{Letter}$", "\u00E9", true)]
    [InlineData(@"^\p{Letter}$", "1", false)]
    [InlineData(@"^\p{Lu}$", ScriptA, true)]
    [InlineData(@"^\p{Ll}$", ScriptA, false)]
    [InlineData(@"^\p{L}$", ScriptA, true)]
    [InlineData(@"^\P{L}$", ScriptA, false)]
    [InlineData(@"^\P{L}$", Grin, true)]
    [InlineData(@"^\p{So}$", Grin, true)]
    [InlineData(@"^\p{Nd}$", AstralDigit, true)]
    [InlineData(@"^\p{Nd}$", "\u0660", true)]
    [InlineData(@"^[\p{Nd}]+$", "1" + AstralDigit, true)]
    [InlineData(@"^[^\p{L}]$", ScriptA, false)]
    [InlineData(@"^[^\p{L}]$", Grin, true)]
    [InlineData(@"^[^\P{L}]$", ScriptA, true)]
    [InlineData(@"^[^\P{L}]$", Grin, false)]
    [InlineData(@"^[\P{Lu}a]$", "A", false)]
    [InlineData(@"^[\P{Lu}a]$", "a", true)]
    [InlineData(@"^\p{LC}$", "a", true)]
    [InlineData(@"^\p{Cased_Letter}$", "A", true)]
    [InlineData(@"^\p{LC}$", ScriptA, true)]
    [InlineData(@"^\p{LC}$", "\u05D0", false)] // an Lo
    [InlineData(@"^\P{LC}$", "\u05D0", true)]
    [InlineData(@"^\P{LC}$", "a", false)]
    [InlineData(@"^[\P{LC}]$", "a", false)]
    [InlineData(@"^\p{C}$", "\u0001", true)]
    [InlineData(@"^\p{Other}$", "a", false)]
    [InlineData(@"^\p{Co}$", "\U000F0000", true)]
    [InlineData(@"^\p{Cn}$", "\U000E0FFF", true)]
    [InlineData(@"^\p{Any}$", Grin, true)]
    [InlineData(@"^\p{Any}$", "\n", true)]
    [InlineData(@"^\P{Any}$", "a", false)]
    [InlineData(@"^\p{ASCII}$", "\u007F", true)]
    [InlineData(@"^\p{ASCII}$", "\u0080", false)]
    [InlineData(@"^\P{ASCII}$", Grin, true)]
    [InlineData(@"^\p{Assigned}$", "a", true)]
    [InlineData(@"^\p{Assigned}$", Grin, true)]
    [InlineData(@"^\p{Assigned}$", "\U000E0FFF", false)]
    public void Properties_cover_all_code_points(string pattern, string input, bool expected) =>
        Assert.Equal(expected, Matches(pattern, input));

    [Theory]
    [InlineData(@"\P{L}")]
    [InlineData(@"^[^\p{L}]$")]
    [InlineData(@"^\p{Assigned}$")]
    [InlineData(@"^[\P{L}]+$")]
    public void Large_properties_still_match_a_newline(string pattern) => Assert.True(Matches(pattern, "\n"));

    [Theory]
    [InlineData("^[a-z]+$")]
    [InlineData(@"^\d{3}-\w+\s.$")]
    [InlineData(@"^\p{Cc}$")]
    public void Simple_patterns_use_the_linear_engine(string pattern) => Assert.True(UsesLinearEngine(pattern));

    [Theory]
    [InlineData("^(?=a)a$")]
    [InlineData("(?<=a)b")]
    [InlineData(@"(a)\1")]
    [InlineData(@"\bfoo")]
    [InlineData(@"\p{L}")]
    public void Other_patterns_use_the_backtracking_engine(string pattern) => Assert.False(UsesLinearEngine(pattern));

    [Fact]
    public void Pattern_with_many_distinct_characters_avoids_the_linear_engine()
    {
        string many = string.Concat(Enumerable.Range(0, 130).Select(i => $"|\\u{0x4E00 + (2 * i):X4}"));
        string pattern = "(?:[^a]" + many + ")";

        Assert.False(UsesLinearEngine(pattern));
        Assert.True(Matches(pattern, "\n"));
    }

    [Theory]
    [InlineData(@"\p{Script=Greek}")]
    [InlineData(@"\p{sc=Latin}")]
    [InlineData(@"\p{Script_Extensions=Greek}")]
    [InlineData(@"\p{scx=Greek}")]
    [InlineData(@"[\P{Script=Greek}]")]
    public void Script_properties_explain_why_they_fail(string pattern)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => PatternCompiler.Compile(pattern));
        Assert.Contains("script", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"\p{Foo}")]
    [InlineData(@"\p{letter}")] // names are case-sensitive
    [InlineData(@"\p{IsGreek}")] // a .NET block, not an ECMA property
    [InlineData(@"\p{Greek}")]
    [InlineData(@"\p{Alphabetic}")]
    [InlineData(@"\p{Emoji}")]
    [InlineData(@"\p{gc=Foo}")]
    [InlineData(@"\p{Foo=Bar}")]
    [InlineData(@"\p{}")]
    [InlineData(@"\p")]
    [InlineData(@"\pL")]
    [InlineData(@"\p{L")]
    public void Unsupported_properties_are_rejected(string pattern)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => PatternCompiler.Compile(pattern));
        Assert.Contains(pattern, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"^\u{1F600}$", Grin, true)]
    [InlineData(@"^\u{1F600}$", Grin2, false)]
    [InlineData(@"^\u{41}$", "A", true)]
    [InlineData(@"^\u{000041}$", "A", true)]
    [InlineData(@"^\u0041$", "A", true)]
    [InlineData(@"^\x41$", "A", true)]
    [InlineData(@"^\uD83D\uDE00$", Grin, true)]
    [InlineData(@"^\uD83D\uDE00+$", Grin + Grin, true)]
    [InlineData(@"^\u{1F600}{2}$", Grin + Grin, true)]
    [InlineData(@"^\u{1F600}{2}$", Grin, false)]
    [InlineData("^" + Grin + "*$", Grin + Grin, true)]
    [InlineData("^" + Grin + "*$", "", true)]
    [InlineData("^" + Grin + "?$", Grin2, false)]
    [InlineData(@"^\u{10FFFF}$", "\U0010FFFF", true)]
    public void Code_point_escapes_are_one_atom(string pattern, string input, bool expected) =>
        Assert.Equal(expected, Matches(pattern, input));

    [Theory]
    [InlineData("^[" + Grin + "]$", Grin, true)]
    [InlineData("^[" + Grin + "]$", Grin2, false)]
    [InlineData("^[^" + Grin + "]$", Grin, false)]
    [InlineData("^[^" + Grin + "]$", Grin2, true)]
    [InlineData("^[^" + Grin + "]$", "a", true)]
    [InlineData("^[a" + Grin + "]+$", "a" + Grin + "a", true)]
    [InlineData("^[a" + Grin + "]$", "b", false)]
    [InlineData(@"^[\u{1F600}-\u{1F64F}]$", Grin2, true)]
    [InlineData(@"^[\u{1F600}-\u{1F64F}]$", ScriptA, false)]
    [InlineData(@"^[\uD83D\uDE00]$", Grin, true)]
    [InlineData(@"^[\u{FFF0}-\u{10010}]$", "\uFFF0", true)]
    [InlineData(@"^[\u{FFF0}-\u{10010}]$", "\U00010010", true)]
    [InlineData(@"^[\u{FFF0}-\u{10010}]$", "\U00010011", false)]
    [InlineData(@"^[\u{10000}-\u{10FFFF}]$", Grin, true)]
    [InlineData(@"^[\u{10000}-\u{10FFFF}]$", "\uFFFF", false)]
    [InlineData(@"^[\u{103FF}-\u{10800}]$", "\U000103FF", true)]
    [InlineData(@"^[\u{103FF}-\u{10800}]$", "\U00010400", true)]
    [InlineData(@"^[\u{103FF}-\u{10800}]$", "\U000107FF", true)]
    [InlineData(@"^[\u{103FF}-\u{10800}]$", "\U00010800", true)]
    [InlineData(@"^[\u{103FF}-\u{10800}]$", "\U00010801", false)]
    [InlineData(@"^[\u{103FF}-\u{10800}]$", "\U000103FE", false)]
    public void Astral_characters_work_in_classes(string pattern, string input, bool expected) =>
        Assert.Equal(expected, Matches(pattern, input));

    [Theory]
    [InlineData("^..$", Grin, false)]
    [InlineData(@"^\D\D$", Grin, false)]
    [InlineData(@"^\W\W$", Grin, false)]
    [InlineData(@"^\S\S$", Grin, false)]
    [InlineData("^[^a][^a]$", Grin, false)]
    [InlineData("^[^]$", Grin, true)]
    [InlineData("^[^][^]$", Grin, false)]
    [InlineData("^.{2}$", Grin + Grin, true)]
    [InlineData("^.{3}$", Grin + Grin, false)]
    public void A_surrogate_pair_is_never_split(string pattern, string input, bool expected) =>
        Assert.Equal(expected, Matches(pattern, input));

    [Fact]
    public void Lone_surrogates_are_valid_but_never_match_well_formed_text()
    {
        Assert.False(Matches(@"\uD83D", Grin));
        Assert.False(Matches(@"\uDE00", Grin));
        Assert.False(Matches(@"[\uD83D]", Grin));
        Assert.False(Matches(@"\u{D83D}\u{DE00}", Grin));
        Assert.False(Matches(@"\p{Surrogate}", Grin));
        Assert.True(Matches(@"^\P{Cs}$", Grin));
    }

    [Theory]
    [InlineData(@"(?!.{2})\B", Grin + "a", false)]
    [InlineData(@"(?<![^])\B", "a" + Grin, false)]
    [InlineData(@"(?<![^])(?![^])", Grin, false)]
    [InlineData(@"(?<![^])(?![^])", "", true)]
    public void Zero_width_patterns_do_not_match_inside_a_pair(string pattern, string input, bool expected) =>
        Assert.Equal(expected, Matches(pattern, input));

    [Theory]
    [InlineData("(?<year>[0-9]{4})-(?<month>[0-9]{2})", "2026-11", true)]
    [InlineData(@"^(?<a>x)\k<a>$", "xx", true)]
    [InlineData(@"^(?<a>x)\k<a>$", "xy", false)]
    [InlineData(@"^\k<a>(?<a>x)$", "x", true)]
    [InlineData(@"^(?<$a>x)\k<$a>$", "xx", true)]
    [InlineData(@"^(?<\u0061>x)\k<a>$", "xx", true)]
    [InlineData(@"^(?<\u{61}b>x)\k<ab>$", "xx", true)]
    [InlineData("^(?<\u00E9>x)\\k<\u00E9>$", "xx", true)]
    [InlineData("^(?<" + ScriptA + ">x)\\k<" + ScriptA + ">$", "xx", true)]
    [InlineData("(?<a>x)|(?<a>y)", "y", true)]
    public void Named_groups_work(string pattern, string input, bool expected) =>
        Assert.Equal(expected, Matches(pattern, input));

    [Fact]
    public void Group_names_survive()
    {
        Match match = PatternCompiler.Compile("(?<year>[0-9]{4})").Match("in 2026");
        Assert.Equal("2026", match.Groups["year"].Value);
    }

    [Theory]
    [InlineData(@"^(a)\1$", "aa", true)]
    [InlineData(@"^(a)\1$", "ab", false)]
    [InlineData(@"^(?<n>a)(b)\1$", "aba", true)] // .NET would number (b) first
    [InlineData(@"^(?<n>a)(b)\2$", "abb", true)]
    [InlineData(@"^(a)(?<n>b)(c)\3$", "abcc", true)]
    [InlineData(@"^\1(a)$", "a", true)] // a group that hasn't matched is the empty string
    [InlineData(@"^(?:(a)|b)\1$", "b", true)]
    [InlineData(@"^(a\1)$", "a", true)]
    [InlineData(@"^(a)\10$", "a", false)]
    [InlineData(@"^(a)(b)(c)(d)(e)(f)(g)(h)(i)(j)\10$", "abcdefghijj", true)]
    [InlineData(@"^(a)\1{2}$", "aaa", true)]
    [InlineData(@"^(a)\1+0$", "aaa0", true)]
    public void Backreferences_follow_ecma_numbering(string pattern, string input, bool expected)
    {
        if (pattern == @"^(a)\10$")
        {
            Assert.Throws<ArgumentException>(() => PatternCompiler.Compile(pattern));
        }
        else
        {
            Assert.Equal(expected, Matches(pattern, input));
        }
    }

    [Theory]
    [InlineData(@"^\cA$", "\u0001")]
    [InlineData(@"^\ca$", "\u0001")]
    [InlineData(@"^\cZ$", "\u001A")]
    [InlineData(@"^\cz$", "\u001A")]
    [InlineData(@"^\cJ$", "\n")]
    [InlineData(@"^[\cC]$", "\u0003")]
    [InlineData(@"^\t$", "\t")]
    [InlineData(@"^\n$", "\n")]
    [InlineData(@"^\v$", "\v")]
    [InlineData(@"^\f$", "\f")]
    [InlineData(@"^\r$", "\r")]
    [InlineData(@"^\0$", "\0")]
    [InlineData(@"^[\0]$", "\0")]
    [InlineData(@"^[\b]$", "\b")]
    [InlineData(@"^[\-]$", "-")]
    public void Control_escapes_work(string pattern, string input) => Assert.True(Matches(pattern, input));

    [Theory]
    [InlineData(@"\/", "/")]
    [InlineData(@"\^", "^")]
    [InlineData(@"\$", "$")]
    [InlineData(@"\\", "\\")]
    [InlineData(@"\.", ".")]
    [InlineData(@"\*", "*")]
    [InlineData(@"\+", "+")]
    [InlineData(@"\?", "?")]
    [InlineData(@"\(", "(")]
    [InlineData(@"\)", ")")]
    [InlineData(@"\[", "[")]
    [InlineData(@"\]", "]")]
    [InlineData(@"\{", "{")]
    [InlineData(@"\}", "}")]
    [InlineData(@"\|", "|")]
    [InlineData(@"[\/]", "/")]
    [InlineData(@"[\]]", "]")]
    [InlineData(@"[\^]", "^")]
    [InlineData("[[]", "[")]
    [InlineData("[$.*+?(){}|]", "|")]
    [InlineData("a/b", "a/b")]
    [InlineData("#", "#")]
    [InlineData(" ", " ")]
    public void Syntax_characters_can_be_escaped(string pattern, string input) => Assert.True(Matches(pattern, input));

    [Theory]
    [InlineData("[]", "", false)]
    [InlineData("[]", "a", false)]
    [InlineData("a[]", "a", false)]
    [InlineData("a|[]", "a", true)]
    [InlineData("[]*", "", true)]
    [InlineData("^[^]$", "a", true)]
    [InlineData("^[^]$", "\n", true)]
    [InlineData("^[^]$", "\u2028", true)]
    [InlineData("^[^]$", "", false)]
    [InlineData("^[^]*$", "a\nb", true)]
    public void Empty_classes_work(string pattern, string input, bool expected) =>
        Assert.Equal(expected, Matches(pattern, input));

    [Fact]
    public void Empty_class_keeps_the_linear_engine() => Assert.True(UsesLinearEngine("[]"));

    [Theory]
    [InlineData(@"^[\d]$", "\u0660", false)]
    [InlineData(@"^[\d]$", "5", true)]
    [InlineData(@"^[^\d]$", "5", false)]
    [InlineData(@"^[^\d]$", "\u0660", true)]
    [InlineData(@"^[\D]$", "5", false)]
    [InlineData(@"^[\D]$", Grin, true)]
    [InlineData(@"^[^\D]$", "5", true)]
    [InlineData(@"^[^\D]$", Grin, false)]
    [InlineData(@"^[\w-]+$", "a-b", true)]
    [InlineData(@"^[\W]$", "\u00E9", true)]
    [InlineData(@"^[\s]$", "\uFEFF", true)]
    [InlineData(@"^[\S]$", "\uFEFF", false)]
    [InlineData(@"^[\s\S]$", "\n", true)]
    [InlineData("^[a-]$", "-", true)]
    [InlineData("^[-a]$", "-", true)]
    [InlineData("^[--a]$", "A", true)]
    [InlineData("^[^^]$", "^", false)]
    [InlineData("^[a^]$", "^", true)]
    [InlineData(@"^[\u0041-\u0043]$", "B", true)]
    public void Classes_use_ecma_semantics(string pattern, string input, bool expected) =>
        Assert.Equal(expected, Matches(pattern, input));

    [Theory]
    [InlineData("^(?=a).$", "a", true)]
    [InlineData("^(?!a).$", "a", false)]
    [InlineData("(?<=a)b", "ab", true)]
    [InlineData("(?<!a)b", "ab", false)]
    [InlineData("^(?:a|b)+$", "abba", true)]
    [InlineData("^a{2}$", "aa", true)]
    [InlineData("^a{2,}$", "aaa", true)]
    [InlineData("^a{1,2}$", "aaa", false)]
    [InlineData("^a*?$", "aa", true)]
    [InlineData("^a+?b??$", "a", true)]
    [InlineData("^()$", "", true)]
    [InlineData("^a||b$", "c", true)]
    [InlineData("", "anything", true)]
    public void Common_syntax_passes_through(string pattern, string input, bool expected) =>
        Assert.Equal(expected, Matches(pattern, input));

    [Theory]
    [InlineData("^a{99999999999}$", "a", false)]
    [InlineData("^a{0,99999999999}$", "aaa", true)]
    [InlineData("^a{1,99999999999}?$", "aaa", true)]
    [InlineData("^a{99999999999,}$", "a", false)]
    public void Huge_counts_are_clamped(string pattern, string input, bool expected) =>
        Assert.Equal(expected, Matches(pattern, input));

    [Theory]
    // Not valid in Unicode mode, even though they are valid .NET or legacy ECMA.
    [InlineData(@"\-")]
    [InlineData(@"\_")]
    [InlineData(@"\a")]
    [InlineData(@"\e")]
    [InlineData(@"\A")]
    [InlineData(@"\Z")]
    [InlineData(@"\z")]
    [InlineData(@"\G")]
    [InlineData(@"\07")]
    [InlineData(@"\1")]
    [InlineData(@"(a)\2")]
    [InlineData(@"\k")]
    [InlineData(@"\k<a>")]
    [InlineData(@"(?<b>a)\k<a>")]
    [InlineData(@"\c")]
    [InlineData(@"\c1")]
    [InlineData(@"[\c1]")]
    [InlineData(@"[\1]")]
    [InlineData(@"[\B]")]
    [InlineData(@"[\k]")]
    [InlineData(@"\x4")]
    [InlineData(@"\xZZ")]
    [InlineData(@"\u004")]
    [InlineData(@"\u{}")]
    [InlineData(@"\u{110000}")]
    [InlineData(@"\u{1F600")]
    [InlineData(@"\u{XYZ}")]
    [InlineData("\\")]
    [InlineData("[\\")]
    [InlineData("a{")]
    [InlineData("a{1")]
    [InlineData("a{,2}")]
    [InlineData("{1}")]
    [InlineData("a{2,1}")]
    [InlineData("a{99999999999,99999999998}")]
    [InlineData("}")]
    [InlineData("]")]
    [InlineData("[")]
    [InlineData("[a")]
    [InlineData("[z-a]")]
    [InlineData(@"[\d-a]")]
    [InlineData(@"[a-\d]")]
    [InlineData("[a-z-[aeiou]]")]
    [InlineData("*")]
    [InlineData("+a")]
    [InlineData("?")]
    [InlineData("a**")]
    [InlineData("a*+")]
    [InlineData("a???")]
    [InlineData("(*)")]
    [InlineData("a|*")]
    [InlineData("^*")]
    [InlineData("$+")]
    [InlineData(@"\b*")]
    [InlineData("(?=a)*")]
    [InlineData("(?<!a)+")]
    [InlineData(")")]
    [InlineData("a)(b")]
    [InlineData("(?i)a")]
    [InlineData("(?i:a)")]
    [InlineData("(?#comment)")]
    [InlineData("(?>a)")]
    [InlineData("(?'n'a)")]
    [InlineData("(?(1)a)")]
    [InlineData("(?")]
    [InlineData("(?<")]
    [InlineData("(?<>a)")]
    [InlineData("(?<1a>a)")]
    [InlineData("(?<a b>a)")]
    [InlineData("(?<a")]
    [InlineData(@"(?<\x41>a)")]
    [InlineData("(?<a>x)(?<a>y)")]
    [InlineData("(?<a>(?<a>x))")]
    [InlineData("(?<a>x)(?:y|(?<a>z))")]
    public void Invalid_patterns_are_rejected_at_declaration(string pattern)
    {
        Assert.ThrowsAny<ArgumentException>(() => PatternCompiler.Compile(pattern));
        Assert.ThrowsAny<ArgumentException>(() => Z.String().Pattern(pattern));
    }

    [Theory]
    [InlineData("(")]
    [InlineData("(a")]
    [InlineData("(?:a")]
    [InlineData("(a|(b)")]
    public void Unclosed_groups_are_left_to_the_dotnet_parser(string pattern) =>
        Assert.Throws<RegexParseException>(() => PatternCompiler.Compile(pattern));

    [Fact]
    public void Error_names_the_pattern_and_position()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => PatternCompiler.Compile(@"ab\-"));

        Assert.Contains(@"'ab\-'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("position 3", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deep_nesting_does_not_overflow_the_stack()
    {
        const int Depth = 100_000;
        string translated = EcmaRegexTranslator.Translate(new string('(', Depth) + new string(')', Depth));

        Assert.Equal(2 * Depth, translated.Length);
    }

    [Fact]
    public void Schema_applies_ecma_semantics_end_to_end()
    {
        StringSchema schema = Z.String().Pattern(@"^\p{Letter}+$");

        Assert.True(schema.SafeParse("\"\\u00e9t\\u00e9\"").IsSuccess);
        Assert.False(schema.SafeParse("\"abc\\n\"").IsSuccess);
    }
}
