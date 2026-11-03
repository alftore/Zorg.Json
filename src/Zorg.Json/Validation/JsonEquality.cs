using System;
using System.Text.Json;

namespace Zorg.Json.Validation;

internal static class JsonEquality
{
    /// <summary>
    /// <see cref="JsonElement.DeepEquals"/> throws on input it can't decode, such as a lone surrogate or a huge
    /// exponent, and that input can reach us from anyone.
    /// </summary>
    public static bool AreEqual(JsonElement left, JsonElement right)
    {
        if (left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number)
        {
            return ExactNumber.Parse(left.GetRawText()).CompareTo(ExactNumber.Parse(right.GetRawText())) == 0;
        }

        try
        {
            return JsonElement.DeepEquals(left, right);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return left.GetRawText() == right.GetRawText();
        }
    }
}
