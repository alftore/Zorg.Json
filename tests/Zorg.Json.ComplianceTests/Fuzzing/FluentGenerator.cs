using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Zorg.Json.Emission;
using Zorg.Json.Sampling;

namespace Zorg.Json.ComplianceTests.Fuzzing;

/// <summary>Reaches a <see cref="Schema{T}"/> without knowing its parsed type.</summary>
internal interface IProbe
{
    public bool ParseText(string json);

    public bool ParseUtf8(byte[] json);

    public bool ParseElement(JsonElement json);

    /// <summary>Parses, then validates the parsed value. Null when parsing fails, else whether Validate accepted it.</summary>
    public bool? Revalidate(JsonElement json);

    public EmitResult Emit(JsonSchemaOptions options);

    public SampleResult Sample();
}

internal sealed class Probe<T>(Schema<T> schema) : IProbe
{
    public bool ParseText(string json) => schema.SafeParse(json).IsSuccess;

    public bool ParseUtf8(byte[] json) => schema.SafeParse(json).IsSuccess;

    public bool ParseElement(JsonElement json) => schema.SafeParse(json).IsSuccess;

    public bool? Revalidate(JsonElement json)
    {
        ParseResult<T> parsed = schema.SafeParse(json);
        return parsed.IsSuccess ? schema.Validate(parsed.Value).IsSuccess : null;
    }

    public EmitResult Emit(JsonSchemaOptions options) => schema.Emit(options);

    public SampleResult Sample() => schema.GenerateSample();
}

/// <summary>A generated schema, with the calls that built it so a failure can be reproduced by hand.</summary>
internal sealed record Built(ISchema Schema, string Code, IProbe Probe)
{
    public static Built Of<T>(Schema<T> schema, string code) => new(schema, code, new Probe<T>(schema));
}

/// <summary>Builds random schemas by calling the fluent API. A call that rejects its argument is skipped.</summary>
internal sealed class FluentGenerator(Random random)
{
    private static readonly string[] Patterns =
    [
        "^a", "a$", "^[a-z]+$", "^(a|b)*$", "\\d{2,4}", "^\\p{L}+$", "(a+)+$", "^(a*)*b$", "(?=a)b", "(a)\\1", "^.{0,5}$",
        "[\\s\\S]*", "^x-", "^\\w+@\\w+$", "(x+x+)+y", "^(([a-z])+.)+[A-Z]([a-z])+$", "[", "(", "*", "\\", "(?<n>a)\\k<n>",
        "^[\\u{1F600}]$", "^\\p{Lu}", "a{1000}", "(a{1,100}){1,100}", "^$", "(?i)a", "[a-z&&[^b]]",
    ];

    private static readonly string[] Formats =
    [
        "email", "uuid", "uri", "date-time", "date", "time", "hostname", "ipv4", "ipv6", "uri-reference", "regex", "json-pointer",
        "idn-email", "idn-hostname", "duration", "iri", "uri-template", "unknown-format", "",
    ];

    private static readonly string[] Strings =
    [
        "", "a", "A", " a ", "ab", "abc", "web", "WEB", "x-a", "circle", "k", "😀", "é", "é", "ß", "İ",
        "ann@example.com", "2026-11-03", "2eb8aa08-aa98-11ea-b4aa-73b441d16380", "\u0000", "   ",
    ];

    private static readonly double[] Numbers =
    [
        0, 1, -1, 0.1, 0.3, 2, 5, 10, 50, 100, 1e10, 2147483647, 255, 0.5, -0.0, 1e300, 3, 7,
    ];

    private static readonly string[] Keys = ["a", "b", "name", "count", "k", "kind", "x-a", "x-b", "", "__proto__", "😀", "radius"];

    private readonly Random _random = random;
    private int _lazy;

    public Built Next(int depth = 0)
    {
        int kind = _random.Next(depth >= 4 ? 5 : 14);

        return kind switch
        {
            0 => String(),
            1 => Number<double>("double", ToDouble),
            2 => Number<long>("long", number => long.CreateTruncating(number)),
            3 => Boolean(),
            4 => Simple(),
            5 => Number<int>("int", number => int.CreateTruncating(number)),
            6 => Number<decimal>("decimal", number => decimal.CreateTruncating(number)),
            7 => Number<byte>("byte", number => byte.CreateTruncating(number)),
            8 or 9 => Object(depth),
            10 => Array(depth),
            11 => Combinator(depth),
            12 => Recursive(depth),
            _ => Union(depth),
        };
    }

    private static double ToDouble(double number) => number;

    private Built Simple()
    {
        return _random.Next(5) switch
        {
            0 => Built.Of(Z.Null(), "Z.Null()"),
            1 => Built.Of(Z.Any(), "Z.Any()"),
            2 => Built.Of(Z.Never(), "Z.Never()"),
            3 => Built.Of(Z.Array(), "Z.Array()"),
            _ => Built.Of(Z.Object(), "Z.Object()"),
        };
    }

    private string Pick(string[] pool)
    {
        return pool[_random.Next(pool.Length)];
    }

    private T Pick<T>(IReadOnlyList<T> pool) => pool[_random.Next(pool.Count)];

    private bool Chance(int percent) => _random.Next(100) < percent;

    private (TSelf Schema, string Code) Common<T, TSelf>(TSelf schema, string code, Func<T> value)
        where TSelf : Schema<T, TSelf>
    {
        try
        {
            switch (_random.Next(14))
            {
                case 0:
                    return (schema.Title("t"), code + ".Title(\"t\")");
                case 1:
                    return (schema.Description("d"), code + ".Description(\"d\")");
                case 2:
                    return (schema.Optional(), code + ".Optional()");
                case 3:
                    T chosen = value();
                    return (schema.Default(chosen), code + $".Default({chosen})");
                case 4:
                    int modulus = _random.Next(2, 4);
                    return (schema.Refine(item => (Convert.ToString(item, CultureInfo.InvariantCulture)?.Length ?? 0) % modulus != 0, "no"), code + $".Refine(len % {modulus} != 0)");
                case 5:
                    string name = "n" + _random.Next(3);
                    return (schema.Named(name), code + $".Named(\"{name}\")");
                case 6:
                    return (schema.Examples(value(), value()), code + ".Examples(..)");
                case 7:
                    return (schema.Meta("x-foo", JsonSerializer.SerializeToElement(1)), code + ".Meta(\"x-foo\", 1)");
                case 8:
                    return (schema.Deprecated(), code + ".Deprecated()");
                case 9:
                    return (schema.ReadOnly(), code + ".ReadOnly()");
                case 10:
                    return (schema.WriteOnly(), code + ".WriteOnly()");
                case 11:
                    return (schema.Comment("c"), code + ".Comment(\"c\")");
                case 12:
                    return (schema.Title(new string('t', 5000)), code + ".Title(5000 chars)");
                default:
                    return (schema, code);
            }
        }
        catch (ArgumentException)
        {
            return (schema, code);
        }
    }

    private Built String()
    {
        StringSchema schema = Z.String();
        string code = "Z.String()";
        int steps = _random.Next(5);

        for (int index = 0; index < steps; index++)
        {
            try
            {
                switch (_random.Next(16))
                {
                    case 0:
                        int min = _random.Next(-1, 6);
                        schema = schema.MinLength(min);
                        code += $".MinLength({min})";
                        break;
                    case 1:
                        int max = _random.Next(-1, 6);
                        schema = schema.MaxLength(max);
                        code += $".MaxLength({max})";
                        break;
                    case 2:
                        string pattern = Pick(Patterns);
                        schema = schema.Pattern(pattern);
                        code += $".Pattern(\"{pattern.Replace("\\", "\\\\", StringComparison.Ordinal)}\")";
                        break;
                    case 3:
                        string format = Pick(Formats);
                        schema = schema.Format(format);
                        code += $".Format(\"{format}\")";
                        break;
                    case 4:
                        string constant = Pick(Strings);
                        schema = schema.Const(constant);
                        code += $".Const({Quote(constant)})";
                        break;
                    case 5:
                        string[] values = [Pick(Strings), Pick(Strings), Pick(Strings)];
                        schema = schema.Enum(values);
                        code += $".Enum({string.Join(", ", values.Select(Quote))})";
                        break;
                    case 6:
                        schema = schema.Nullable();
                        code += ".Nullable()";
                        break;
                    case 7:
                        (schema, code) = Normalizer(schema, code);
                        break;
                    case 10:
                        string ecma = Pick(Patterns);
                        schema = schema.Pattern(new System.Text.RegularExpressions.Regex(ecma, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100)), Chance(50) ? ecma : null);
                        code += $".Pattern(new Regex({Quote(ecma)}))";
                        break;
                    case 8:
                        NormalizationForm form = Pick(new[] { NormalizationForm.FormC, NormalizationForm.FormD, NormalizationForm.FormKC, NormalizationForm.FormKD });
                        schema = schema.NormalizeUnicode(form);
                        code += $".NormalizeUnicode({form})";
                        break;
                    case 9:
                        int which = _random.Next(3);
                        schema = schema.Overwrite(which == 0 ? static text => text + "x" : which == 1 ? static _ => string.Empty : static text => text.Replace("a", string.Empty, StringComparison.Ordinal));
                        code += $".Overwrite({which})";
                        break;
                    default:
                        (schema, code) = Common<string, StringSchema>(schema, code, () => Pick(Strings));
                        break;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return Built.Of(schema, code);
    }

    private (StringSchema, string) Normalizer(StringSchema schema, string code) => _random.Next(6) switch
    {
        0 => (schema.Trim(), code + ".Trim()"),
        1 => (schema.TrimStart(), code + ".TrimStart()"),
        2 => (schema.TrimEnd(), code + ".TrimEnd()"),
        3 => (schema.ToLowerInvariant(), code + ".ToLowerInvariant()"),
        4 => (schema.ToUpperInvariant(), code + ".ToUpperInvariant()"),
        _ => (schema.Trim().ToLowerInvariant(), code + ".Trim().ToLowerInvariant()"),
    };

    private Built Number<TNumber>(string name, Func<double, TNumber> convert)
        where TNumber : struct, INumber<TNumber>
    {
        NumberSchema<TNumber> schema = name switch
        {
            "double" => (NumberSchema<TNumber>)(object)Z.Number(),
            "long" => (NumberSchema<TNumber>)(object)Z.Integer(),
            _ => Z.Number<TNumber>(),
        };

        string code = name switch
        {
            "double" => "Z.Number()",
            "long" => "Z.Integer()",
            _ => $"Z.Number<{name}>()",
        };

        TNumber Value() => convert(Pick(Numbers));
        int steps = _random.Next(5);

        for (int index = 0; index < steps; index++)
        {
            try
            {
                TNumber value = Value();
                string text = value.ToString(null, CultureInfo.InvariantCulture);

                switch (_random.Next(12))
                {
                    case 0:
                        schema = schema.Minimum(value);
                        code += $".Minimum({text})";
                        break;
                    case 1:
                        schema = schema.Maximum(value);
                        code += $".Maximum({text})";
                        break;
                    case 2:
                        schema = schema.ExclusiveMinimum(value);
                        code += $".ExclusiveMinimum({text})";
                        break;
                    case 3:
                        schema = schema.ExclusiveMaximum(value);
                        code += $".ExclusiveMaximum({text})";
                        break;
                    case 4:
                        schema = schema.MultipleOf(value);
                        code += $".MultipleOf({text})";
                        break;
                    case 5:
                        schema = schema.Positive();
                        code += ".Positive()";
                        break;
                    case 6:
                        schema = schema.Negative();
                        code += ".Negative()";
                        break;
                    case 7:
                        schema = schema.Const(value);
                        code += $".Const({text})";
                        break;
                    case 8:
                        schema = schema.Enum(value, Value());
                        code += $".Enum({text}, ..)";
                        break;
                    case 9:
                        schema = schema.Coerce();
                        code += ".Coerce()";
                        break;
                    case 10:
                        NullableSchema<TNumber> nullable = schema.Nullable();
                        return Built.Of(nullable, code + ".Nullable()");
                    default:
                        (schema, code) = Common<TNumber, NumberSchema<TNumber>>(schema, code, Value);
                        break;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return Built.Of(schema, code);
    }

    private Built Boolean()
    {
        BooleanSchema schema = Z.Boolean();
        string code = "Z.Boolean()";

        for (int index = 0; index < _random.Next(3); index++)
        {
            switch (_random.Next(4))
            {
                case 0:
                    schema = schema.Const(Chance(50));
                    code += ".Const(..)";
                    break;
                case 1:
                    schema = schema.Coerce();
                    code += ".Coerce()";
                    break;
                default:
                    (schema, code) = Common<bool, BooleanSchema>(schema, code, () => Chance(50));
                    break;
            }
        }

        return Built.Of(schema, code);
    }

    private Built Array(int depth)
    {
        Built item = Next(depth + 1);

        return item.Schema switch
        {
            Schema<JsonElement> element => ArrayOf(Z.Array(element), $"Z.Array({item.Code})", item),
            Schema<string> text => ArrayOf(Z.Array(text), $"Z.Array({item.Code})", item),
            Schema<long> number => ArrayOf(Z.Array(number), $"Z.Array({item.Code})", item),
            Schema<double> number => ArrayOf(Z.Array(number), $"Z.Array({item.Code})", item),
            Schema<bool> boolean => ArrayOf(Z.Array(boolean), $"Z.Array({item.Code})", item),
            _ => ArrayOf(Z.Array(), "Z.Array()", item),
        };
    }

    private Built ArrayOf<TItem>(ArraySchema<TItem> schema, string code, Built item)
    {
        int steps = _random.Next(5);

        for (int index = 0; index < steps; index++)
        {
            try
            {
                switch (_random.Next(8))
                {
                    case 0:
                        int min = _random.Next(-1, 4);
                        schema = schema.MinItems(min);
                        code += $".MinItems({min})";
                        break;
                    case 1:
                        int max = _random.Next(-1, 4);
                        schema = schema.MaxItems(max);
                        code += $".MaxItems({max})";
                        break;
                    case 2:
                        schema = schema.NonEmpty();
                        code += ".NonEmpty()";
                        break;
                    case 3:
                        schema = schema.UniqueItems();
                        code += ".UniqueItems()";
                        break;
                    case 4:
                        int? minimum = Chance(50) ? _random.Next(-1, 3) : null;
                        int? maximum = Chance(50) ? _random.Next(-1, 3) : null;
                        schema = schema.Contains(item.Schema, minimum, maximum);
                        code += $".Contains({item.Code}, {minimum}, {maximum})";
                        break;
                    case 5:
                        schema = schema.Nullable();
                        code += ".Nullable()";
                        break;
                    default:
                        (schema, code) = Common<IReadOnlyList<TItem>, ArraySchema<TItem>>(schema, code, () => new List<TItem>());
                        break;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return Built.Of(schema, code);
    }

    private Built Object(int depth)
    {
        ObjectSchema schema = Chance(10) ? Z.Record(Next(depth + 1).Schema) : Z.Object();
        string code = "Z.Object()";
        int steps = _random.Next(7);

        for (int index = 0; index < steps; index++)
        {
            try
            {
                switch (_random.Next(17))
                {
                    case 0 or 1 or 2 or 3:
                        string key = Pick(Keys);
                        Built child = Next(depth + 1);
                        schema = schema.Property(key, child.Schema);
                        code += $".Property({Quote(key)}, {child.Code})";
                        break;
                    case 4:
                        schema = schema.Strict();
                        code += ".Strict()";
                        break;
                    case 5:
                        schema = schema.Strip();
                        code += ".Strip()";
                        break;
                    case 6:
                        schema = schema.Passthrough();
                        code += ".Passthrough()";
                        break;
                    case 7:
                        Built additional = Next(depth + 1);
                        schema = schema.AdditionalProperties(additional.Schema);
                        code += $".AdditionalProperties({additional.Code})";
                        break;
                    case 8:
                        string pattern = Pick(Patterns);
                        Built patterned = Next(depth + 1);
                        schema = schema.PatternProperty(pattern, patterned.Schema);
                        code += $".PatternProperty({Quote(pattern)}, {patterned.Code})";
                        break;
                    case 9:
                        Built names = String();
                        schema = schema.PropertyNames((StringSchema)names.Schema);
                        code += $".PropertyNames({names.Code})";
                        break;
                    case 10:
                        int min = _random.Next(-1, 4);
                        schema = schema.MinProperties(min);
                        code += $".MinProperties({min})";
                        break;
                    case 11:
                        int max = _random.Next(-1, 4);
                        schema = schema.MaxProperties(max);
                        code += $".MaxProperties({max})";
                        break;
                    case 12:
                        string owner = Pick(Keys);
                        string[] needed = [Pick(Keys), Pick(Keys)];
                        schema = schema.DependentRequired(owner, needed);
                        code += $".DependentRequired({Quote(owner)}, {string.Join(", ", needed.Select(Quote))})";
                        break;
                    case 13:
                        string[] names2 = [Pick(Keys), Pick(Keys)];
                        schema = Chance(50) ? schema.Pick(names2) : schema.Omit(names2);
                        code += $".PickOrOmit({string.Join(", ", names2.Select(Quote))})";
                        break;
                    case 14:
                        schema = Chance(50) ? schema.Partial() : schema.Extend((ObjectSchema)Object(depth + 1).Schema);
                        code += ".PartialOrExtend()";
                        break;
                    case 15:
                        schema = schema.Nullable();
                        code += ".Nullable()";
                        break;
                    default:
                        (schema, code) = Common<JsonElement, ObjectSchema>(schema, code, () => JsonSerializer.SerializeToElement(new { a = 1 }));
                        break;
                }
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidCastException)
            {
            }
        }

        return Built.Of(schema, code);
    }

    private Built Combinator(int depth)
    {
        Built[] branches = [.. Enumerable.Range(0, _random.Next(1, 4)).Select(_ => Next(depth + 1))];
        ISchema[] schemas = [.. branches.Select(branch => branch.Schema)];
        string list = string.Join(", ", branches.Select(branch => branch.Code));

        switch (_random.Next(8))
        {
            case 0:
                return Built.Of(Z.AnyOf(schemas), $"Z.AnyOf({list})");
            case 1:
                return Built.Of(Z.OneOf(schemas), $"Z.OneOf({list})");
            case 2:
                return Built.Of(Z.AllOf(schemas), $"Z.AllOf({list})");
            case 3:
                return Built.Of(Z.Not(schemas[0]), $"Z.Not({branches[0].Code})");
            case 4:
                ISchema condition = schemas[0];
                ISchema? then = schemas.Length > 1 ? schemas[1] : null;
                ISchema? otherwise = schemas.Length > 2 ? schemas[2] : null;
                return Built.Of(Z.If(condition, then, otherwise), $"Z.If({list})");
            case 5:
                return Built.Of(Z.Tuple(schemas), $"Z.Tuple({list})");
            case 6:
                JsonValueSchema nested = Z.AnyOf(Z.Not(schemas[0]), Z.OneOf(schemas));
                return Built.Of(nested, $"Z.AnyOf(Z.Not({branches[0].Code}), Z.OneOf({list}))");
            default:
                JsonValueSchema chosen = Z.AllOf(schemas[0], Z.AnyOf(schemas));
                return Built.Of(chosen, $"Z.AllOf({branches[0].Code}, Z.AnyOf({list}))");
        }
    }

    private Built Union(int depth)
    {
        string key = Pick(new[] { "kind", "k", "a", "" });
        int count = _random.Next(1, 4);
        List<ISchema> branches = [];
        List<string> codes = [];

        for (int index = 0; index < count; index++)
        {
            string value = Pick(new[] { "circle", "square", "a", "b", " a " });
            ObjectSchema branch = Z.Object();
            string code = "Z.Object()";

            if (Chance(85))
            {
                StringSchema discriminator = Chance(80) ? Z.String().Const(value) : Z.String().Trim().Const(value);
                branch = branch.Property(key, discriminator);
                code += $".Property({Quote(key)}, Const({Quote(value)}))";
            }

            if (Chance(70))
            {
                Built extra = Next(depth + 2);
                branch = branch.Property("radius", extra.Schema);
                code += $".Property(\"radius\", {extra.Code})";
            }

            branches.Add(branch);
            codes.Add(code);
        }

        return Built.Of(Z.DiscriminatedUnion(key, [.. branches]), $"Z.DiscriminatedUnion({Quote(key)}, {string.Join(", ", codes)})");
    }

    private Built Recursive(int depth)
    {
        string name = "r" + _lazy++;
        ISchema? target = null;
        JsonValueSchema lazy = Z.Lazy(name, () => target!);
        Built leaf = Next(depth + 2);

        switch (_random.Next(4))
        {
            case 0:
                target = Z.Object().Property("v", leaf.Schema).Property("next", Z.AnyOf(Z.Null(), lazy));
                return Built.Of(lazy, $"Lazy({name}) -> list of {leaf.Code}");
            case 1:
                target = Z.Object().Property("v", leaf.Schema).Property("children", Z.Array(lazy).Optional());
                return Built.Of(lazy, $"Lazy({name}) -> tree of {leaf.Code}");
            case 2:
                target = Z.AnyOf(leaf.Schema, Z.Array(lazy));
                return Built.Of(lazy, $"Lazy({name}) -> nested arrays of {leaf.Code}");
            default:
                target = Z.Object().Passthrough().AdditionalProperties(lazy).Property("v", leaf.Schema);
                return Built.Of(lazy, $"Lazy({name}) -> map of {leaf.Code}");
        }
    }

    private static string Quote(string text)
    {
        StringBuilder builder = new("\"");

        foreach (char character in text)
        {
            builder.Append(character is >= ' ' and <= '~' and not ('"' or '\\') ? character.ToString() : $"\\u{(int)character:x4}");
        }

        return builder.Append('"').ToString();
    }
}
