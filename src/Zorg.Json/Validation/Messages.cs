using System.Globalization;
using System.Resources;

namespace Zorg.Json.Validation;

internal static class Messages
{
    private static readonly ResourceManager Resources = new("Zorg.Json.Resources.Messages", typeof(Messages).Assembly);

    public static string Get(string key, params object?[] arguments)
    {
        string format = Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;
        return string.Format(CultureInfo.CurrentCulture, format, arguments);
    }
}
