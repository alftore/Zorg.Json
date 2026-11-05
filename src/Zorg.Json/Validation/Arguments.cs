using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Zorg.Json.Validation;

internal static class Arguments
{
    /// <summary>
    /// Rejects text with an unpaired surrogate. JSON text is UTF-8 and the emitted schema is serialized as UTF-8, so such
    /// a name or value would be replaced or fail later, far from the mistake.
    /// </summary>
    public static void WellFormed(string value, [CallerArgumentExpression(nameof(value))] string? parameter = null)
    {
        if (JsonText.HasLoneSurrogate(value))
        {
            throw new ArgumentException("The text has an unpaired surrogate, which can't be written as JSON.", parameter);
        }
    }

    /// <summary>Rejects a JSON value that holds a string with an unpaired surrogate escape, such as <c>"\ud800"</c>.</summary>
    public static void WellFormed(JsonElement value, [CallerArgumentExpression(nameof(value))] string? parameter = null)
    {
        if (JsonText.HasUndecodableString(JsonMarshal.GetRawUtf8Value(value)))
        {
            throw new ArgumentException("The value has a string with an unpaired surrogate, which can't be written as JSON.", parameter);
        }
    }
}
