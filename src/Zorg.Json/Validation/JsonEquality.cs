using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Zorg.Json.Validation;

/// <summary>
/// JSON equality as JSON Schema defines it: <c>1</c> equals <c>1.0</c>, object order doesn't matter and strings compare by
/// their decoded text. It never throws and never recurses, so it is safe on any element, however deep, and on strings
/// <see cref="JsonElement.DeepEquals"/> can't decode.
/// </summary>
internal static class JsonEquality
{
    /// <summary>
    /// Compares two values. A number whose exponent is too long to hold can't be compared, so with a <paramref name="context"/>
    /// that ends the parse as a failure and the result is false. The context is also charged for long numbers.
    /// </summary>
    public static bool AreEqual(JsonElement left, JsonElement right, ValidationContext? context = null)
    {
        Stack<(JsonElement Left, JsonElement Right)> pending = new();
        pending.Push((left, right));

        while (pending.Count > 0)
        {
            (JsonElement first, JsonElement second) = pending.Pop();

            if (first.ValueKind != second.ValueKind && !(IsBoolean(first) && IsBoolean(second)))
            {
                return false;
            }

            switch (first.ValueKind)
            {
                case JsonValueKind.True or JsonValueKind.False:
                    if (first.ValueKind != second.ValueKind)
                    {
                        return false;
                    }

                    break;

                case JsonValueKind.Number:
                    if (!NumbersEqual(first, second, context))
                    {
                        return false;
                    }

                    break;

                case JsonValueKind.String:
                    if (!StringsEqual(first, second))
                    {
                        return false;
                    }

                    break;

                case JsonValueKind.Array:
                    if (first.GetArrayLength() != second.GetArrayLength())
                    {
                        return false;
                    }

                    JsonElement.ArrayEnumerator other = second.EnumerateArray().GetEnumerator();

                    foreach (JsonElement item in first.EnumerateArray())
                    {
                        other.MoveNext();
                        pending.Push((item, other.Current));
                    }

                    break;

                case JsonValueKind.Object:
                    if (!PushMembers(first, second, pending))
                    {
                        return false;
                    }

                    break;
            }
        }

        return true;
    }

    /// <summary>
    /// A hash that agrees with <see cref="AreEqual"/>: equal values hash equally, whatever the number's spelling or the
    /// order of an object's properties. It reads every value once and doesn't recurse.
    /// </summary>
    public static int Hash(JsonElement element)
    {
        if (element.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
        {
            return Leaf(element);
        }

        Stack<Frame> frames = new();
        frames.Push(new Frame(element));

        while (true)
        {
            Frame frame = frames.Peek();

            if (frame.Next(out JsonElement child, out string? name))
            {
                if (child.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                {
                    frame.Pending = name;
                    frames.Push(new Frame(child));
                }
                else
                {
                    frame.Add(name, Leaf(child));
                }

                continue;
            }

            int hash = frame.Finish();
            frames.Pop();

            if (frames.Count == 0)
            {
                return hash;
            }

            Frame parent = frames.Peek();
            parent.Add(parent.Pending, hash);
        }
    }

    private static bool NumbersEqual(JsonElement first, JsonElement second, ValidationContext? context)
    {
        string left = first.GetRawText();
        string right = second.GetRawText();

        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }

        if (context is not null && !context.Spend(((left.Length + right.Length) / 32) + 1))
        {
            return false;
        }

        ExactNumber a = ExactNumber.Parse(left);
        ExactNumber b = ExactNumber.Parse(right);

        if (a.IsOutOfRange || b.IsOutOfRange)
        {
            context?.ReportTooComplex("const");
            return false;
        }

        return a.CompareTo(b) == 0;
    }

    private static bool IsBoolean(JsonElement element) => element.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static bool StringsEqual(JsonElement first, JsonElement second)
    {
        ReadOnlySpan<byte> left = JsonText.RawString(first);
        ReadOnlySpan<byte> right = JsonText.RawString(second);

        if (left.IndexOf((byte)'\\') < 0 && right.IndexOf((byte)'\\') < 0)
        {
            return left.SequenceEqual(right);
        }

        return string.Equals(JsonText.GetString(first), JsonText.GetString(second), StringComparison.Ordinal);
    }

    private static bool PushMembers(JsonElement first, JsonElement second, Stack<(JsonElement Left, JsonElement Right)> pending)
    {
        Dictionary<string, JsonElement> left = JsonText.Index(first);
        Dictionary<string, JsonElement> right = JsonText.Index(second);

        if (left.Count != right.Count)
        {
            return false;
        }

        foreach ((string name, JsonElement value) in left)
        {
            if (!right.TryGetValue(name, out JsonElement match))
            {
                return false;
            }

            pending.Push((value, match));
        }

        return true;
    }

    private static int Leaf(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => JsonText.GetString(element).GetHashCode(StringComparison.Ordinal),
        JsonValueKind.Number => Number(element),
        JsonValueKind.True => 1,
        JsonValueKind.False => 2,
        _ => 3,
    };

    private static int Number(JsonElement element)
    {
        return ExactNumber.Parse(element.GetRawText()).GetHash();
    }

    private static int Mix(string? name, int value) => HashCode.Combine(name?.GetHashCode(StringComparison.Ordinal) ?? 0, value);

    private sealed class Frame
    {
        private readonly bool _isObject;
        private JsonElement.ArrayEnumerator _array;
        private JsonElement.ObjectEnumerator _object;
        private readonly Dictionary<string, int>? _members;
        private int _hash;

        public Frame(JsonElement element)
        {
            _isObject = element.ValueKind == JsonValueKind.Object;
            _hash = _isObject ? 7 : 5;

            if (_isObject)
            {
                _object = element.EnumerateObject().GetEnumerator();
                _members = new(StringComparer.Ordinal);
            }
            else
            {
                _array = element.EnumerateArray().GetEnumerator();
            }
        }

        public string? Pending { get; set; }

        public bool Next(out JsonElement child, out string? name)
        {
            if (_isObject)
            {
                if (_object.MoveNext())
                {
                    child = _object.Current.Value;
                    name = JsonText.GetName(_object.Current);
                    return true;
                }
            }
            else if (_array.MoveNext())
            {
                child = _array.Current;
                name = null;
                return true;
            }

            child = default;
            name = null;
            return false;
        }

        public void Add(string? name, int value)
        {
            if (_isObject)
            {
                _members![name!] = value;
            }
            else
            {
                _hash = HashCode.Combine(_hash, value);
            }
        }

        public int Finish()
        {
            if (_isObject)
            {
                int sum = 0;

                foreach ((string name, int value) in _members!)
                {
                    sum += Mix(name, value);
                }

                return HashCode.Combine(_hash, sum, _members.Count);
            }

            return _hash;
        }
    }
}
