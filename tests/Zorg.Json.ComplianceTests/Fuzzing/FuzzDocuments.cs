using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zorg.Json.ComplianceTests.Fuzzing;

/// <summary>One input: text for the string overloads and bytes for the UTF-8 overload. Either may be missing.</summary>
internal sealed record FuzzDocument(string? Text, byte[]? Utf8)
{
    public static FuzzDocument FromText(string text) => new(text, TryEncode(text));

    public static FuzzDocument FromBytes(params byte[] bytes) => new(null, bytes);

    public override string ToString()
    {
        string shown = Text ?? "bytes:" + Convert.ToHexString(Utf8!);
        return shown.Length > 200 ? shown[..200] + $"...({shown.Length} chars)" : shown;
    }

    private static byte[]? TryEncode(string text)
    {
        try
        {
            return new UTF8Encoding(false, true).GetBytes(text);
        }
        catch (EncoderFallbackException)
        {
            return null;
        }
    }
}

internal static class FuzzDocuments
{
    private static readonly string[] Scalars =
    [
        "null", "true", "false", "0", "1", "-1", "1.0", "1.5", "2", "3", "10", "1e2", "5", "50", "51", "2147483648", "0.1", "0.3", "-0", "-0.0", "255", "256",
        "\"\"", "\"a\"", "\" a \"", "\"ab\"", "\"abc\"", "\"true\"", "\"1\"", "\"42\"", "\"x\"", "\"circle\"", "\"square\"", "\"😀\"", "\"e\\u0301\"",
        "\"ann@example.com\"", "\"2026-11-03\"", "\"2eb8aa08-aa98-11ea-b4aa-73b441d16380\"", "\"x-a\"", "\"WEB\"", "\"   \"",
        "\"\\ud800\"", "\"a\\udc00\"", "\"\\ud83d\\ude00\"", "1e999999999", "-1e999999999", "1e-999999999", "1E400", "123456789012345678901234567890",
        "9223372036854775808", "-9223372036854775809", "1.7976931348623157e309", "4.9e-325", "0.1e1", "1.0e0", "0e0", "0.30000000000000004",
    ];

    private static readonly string[] Keys = ["a", "b", "name", "count", "k", "kind", "x-a", "x-b", "", "radius", "v", "next", "children", "__proto__", "\\ud800", "😀", "A"];

    public static FuzzDocument Random(Random random, int depth = 0) => FuzzDocument.FromText(Text(random, depth));

    public static string Text(Random random, int depth)
    {
        int kind = random.Next(depth < 4 ? 5 : 2);

        return kind switch
        {
            2 or 4 when random.Next(4) == 0 => HostileScalar(random),
            2 => "[" + string.Join(',', Enumerable.Range(0, random.Next(4)).Select(_ => Text(random, depth + 1))) + "]",
            3 or 4 => "{" + string.Join(',', Keys.OrderBy(_ => random.Next()).Take(random.Next(5)).Select(key => $"\"{key}\":{Text(random, depth + 1)}")) + "}",
            _ => Scalars[random.Next(Scalars.Length)],
        };
    }

    public static IEnumerable<FuzzDocument> Fixed()
    {
        foreach (string scalar in Scalars)
        {
            yield return FuzzDocument.FromText(scalar);
        }

        foreach (string text in Structural)
        {
            yield return FuzzDocument.FromText(text);
        }

        yield return FuzzDocument.FromText("{\"a\":\"\ud800\"}");
        yield return FuzzDocument.FromText("\"\ud800\"");
        yield return FuzzDocument.FromText("{\"\udc00\":1}");

        foreach (byte[] bytes in InvalidUtf8)
        {
            yield return FuzzDocument.FromBytes(bytes);
        }
    }

    /// <summary>Large and deep inputs. Slower to run, so a run takes a few of them, not all.</summary>
    public static IEnumerable<FuzzDocument> Heavy(Random random)
    {
        int pick = random.Next(10);

        yield return pick switch
        {
            0 => FuzzDocument.FromText(new string('[', 200) + new string(']', 200)),
            1 => FuzzDocument.FromText(string.Concat(Enumerable.Repeat("{\"a\":", 70)) + "1" + new string('}', 70)),
            2 => FuzzDocument.FromText("\"" + new string('a', 300_000) + "\""),
            3 => FuzzDocument.FromText("[" + string.Join(',', Enumerable.Repeat("\"ab\"", 3000)) + "]"),
            4 => FuzzDocument.FromText("{" + string.Join(',', Enumerable.Range(0, 2000).Select(index => $"\"x-{index}\":\"a\"")) + "}"),
            5 => FuzzDocument.FromText("1" + new string('0', 5000)),
            6 => FuzzDocument.FromText("0." + new string('0', 5000) + "1"),
            7 => FuzzDocument.FromText("[" + string.Join(',', Enumerable.Range(0, 3000)) + "]"),
            8 => FuzzDocument.FromText("\"" + string.Concat(Enumerable.Repeat("\\ud83d\\ude00", 20_000)) + "\""),
            _ => FuzzDocument.FromText("[" + string.Join(',', Enumerable.Range(0, 3000).Select(index => $"\"{new string('a', index % 40)}\"")) + "]"),
        };
    }

    /// <summary>Changes one part of a valid document, so it stays close to what a schema accepts.</summary>
    public static IEnumerable<FuzzDocument> Mutations(Random random, string valid)
    {
        for (int index = 0; index < 4; index++)
        {
            JsonNode? copy;

            try
            {
                copy = JsonNode.Parse(valid);
            }
            catch (JsonException)
            {
                yield break;
            }

            string? mutated = Serialize(Mutate(random, ref copy));

            if (mutated is not null)
            {
                yield return FuzzDocument.FromText(mutated);
            }
        }
    }

    private static string? Serialize(JsonNode node)
    {
        try
        {
            return node.ToJsonString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static JsonNode Mutate(Random random, ref JsonNode? node)
    {
        if (node is JsonObject obj && obj.Count > 0 && random.Next(3) != 0)
        {
            string key = obj.ElementAt(random.Next(obj.Count)).Key;
            JsonNode? child = obj[key]?.DeepClone();

            switch (random.Next(4))
            {
                case 0:
                    obj.Remove(key);
                    break;
                case 1:
                    obj[key] = JsonNode.Parse(Scalars[random.Next(Scalars.Length)]);
                    break;
                case 2:
                    obj["extra" + random.Next(3)] = 1;
                    break;
                default:
                    _ = Mutate(random, ref child);
                    obj[key] = child;
                    break;
            }
        }
        else if (node is JsonArray array && array.Count > 0 && random.Next(3) != 0)
        {
            int at = random.Next(array.Count);
            JsonNode? child = array[at]?.DeepClone();

            switch (random.Next(3))
            {
                case 0:
                    array.RemoveAt(at);
                    break;
                case 1:
                    array.Add(JsonNode.Parse(array[at]?.ToJsonString() ?? "null"));
                    break;
                default:
                    _ = Mutate(random, ref child);
                    array[at] = child;
                    break;
            }
        }
        else
        {
            node = JsonNode.Parse(Scalars[random.Next(Scalars.Length)]);
        }

        return node ?? JsonValue.Create(0)!;
    }

    private static string HostileScalar(Random random) => random.Next(5) switch
    {
        0 => "1e" + random.Next(100, 1_000_000_000).ToString(CultureInfo.InvariantCulture),
        1 => "-" + new string('9', random.Next(20, 400)),
        2 => "0." + new string('0', random.Next(20, 400)) + "1",
        3 => "\"" + new string('\u00e9', random.Next(1, 300)) + "\"",
        _ => Scalars[random.Next(Scalars.Length)],
    };

    private static readonly string[] Structural =
    [
        "", " ", "\n", "nul", "[", "]", "{", "}", "[1,]", "{,}", "{\"a\":}", "{\"a\" 1}", "'a'", "NaN", "Infinity", "-Infinity", "+1", "01", "1.", ".5", "0x10", "1e", "1e+", "--1",
        "/*c*/1", "// c\n1", "[1,2,]", "{\"a\":1,}", "\ufeff1", "\"\\u00\"", "\"\\x\"", "\"\n\"", "\"\t\"", "\"\0\"", "{\"a\":1,\"a\":2}", "{\"a\":1,\"a\":1}", "{\"a\":{\"b\":1},\"a\":{\"b\":2}}",
        "{\"A\":1,\"a\":2}", "{\"k\":\"a\",\"k\":\"b\"}", "{\"\":1,\"\":2}", "[] []", "1 2", "null null", "{}{}", "\"a\"\"b\"", "true false", "[[[[]]]]",
        "{\"kind\":\"circle\",\"radius\":1}", "{\"kind\":\"square\"}", "{\"kind\":\"circle\",\"kind\":\"square\"}", "{\"k\":\"a\"}", "{\"k\":\" a \"}", "{\"name\":\"a\",\"count\":1}",
        "{\"name\":\"a\",\"count\":1.5}", "{\"name\":\"a\",\"count\":1e2}", "{\"name\":\"a\",\"count\":2147483648}", "{\"name\":null}", "{\"x-a\":\"  a  \"}", "{\"a\":1,\"b\":2}",
        "{\"v\":1,\"next\":{\"v\":2,\"next\":null}}", "{\"v\":1,\"children\":[{\"v\":2}]}", "[[[\"a\"]],\"a\"]", "[1,1]", "[\"a\",\"a\"]", "[1,\"1\"]", "[{\"a\":1},{\"a\":1}]", "[0.1,0.10]", "[1,1.0]",
    ];

    private static readonly byte[][] InvalidUtf8 =
    [
        [0x22, 0xFF, 0x22],
        [0x22, 0xC0, 0x80, 0x22],
        [0x22, 0xE2, 0x82, 0x22],
        [0x22, 0xED, 0xA0, 0x80, 0x22],
        [0x22, 0xF4, 0x90, 0x80, 0x80, 0x22],
        [0x7B, 0x22, 0xFF, 0x22, 0x3A, 0x31, 0x7D],
        [0xEF, 0xBB, 0xBF, 0x31],
        [0xFF, 0xFE, 0x31, 0x00],
        [0x31, 0x00],
        [0x22, 0x61],
        [0x5B, 0x22, 0xE2, 0x82],
        [],
    ];
}
