using System;
using System.Globalization;
using System.Net.Mail;
using System.Text.RegularExpressions;

namespace Zorg.Json.Validation;

internal static partial class StringFormats
{
    /// <summary>Gets the check for a format. Null means the format is only an annotation.</summary>
    public static Func<string, bool>? Find(string? format) => format switch
    {
        "email" => IsEmail,
        "uuid" => IsUuid,
        "uri" => IsUri,
        "date-time" => IsDateTime,
        "date" => IsDate,
        _ => null,
    };

    private static bool IsEmail(string value) =>
        MailAddress.TryCreate(value, out MailAddress? address) && address.Address == value;

    private static bool IsUuid(string value) => Guid.TryParseExact(value, "D", out _);

    private static bool IsUri(string value) => Uri.IsWellFormedUriString(value, UriKind.Absolute);

    private static bool IsDateTime(string value) =>
        DateTimeShape().IsMatch(value)
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static bool IsDate(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}[Tt][0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]+)?([Zz]|[+-][0-9]{2}:[0-9]{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex DateTimeShape();
}
