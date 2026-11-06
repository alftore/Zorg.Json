using System;
using Xunit;

namespace Zorg.Json.Tests;

public sealed class StringFormatShortcutTests
{
    public static TheoryData<string, Func<StringSchema, StringSchema>, string, string> Cases => new()
    {
        { "time", s => s.Time(), "\"10:20:30Z\"", "\"10:20\"" },
        { "duration", s => s.Duration(), "\"P1DT2H\"", "\"1 day\"" },
        { "hostname", s => s.Hostname(), "\"example.com\"", "\"-bad-.com\"" },
        { "idn-hostname", s => s.IdnHostname(), "\"bücher.example\"", "\"-bad-.com\"" },
        { "idn-email", s => s.IdnEmail(), "\"jörg@example.com\"", "\"nobody\"" },
        { "ipv4", s => s.Ipv4(), "\"192.168.0.1\"", "\"192.168.0.256\"" },
        { "ipv6", s => s.Ipv6(), "\"::1\"", "\"1::2::3\"" },
        { "iri", s => s.Iri(), "\"https://例え.jp/a\"", "\"/relative\"" },
        { "iri-reference", s => s.IriReference(), "\"/例え\"", "\"http://[bad\"" },
        { "uri-reference", s => s.UriReference(), "\"/a/b?c\"", "\"http://[bad\"" },
        { "uri-template", s => s.UriTemplate(), "\"/users/{id}\"", "\"/users/{id\"" },
        { "json-pointer", s => s.JsonPointer(), "\"/a/0\"", "\"a/0\"" },
        { "relative-json-pointer", s => s.RelativeJsonPointer(), "\"1/a\"", "\"/a\"" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_shortcut_sets_its_format_and_enforces_it(string format, Func<StringSchema, StringSchema> shortcut, string valid, string invalid)
    {
        StringSchema schema = shortcut(Z.String());

        Assert.Equal(format, schema.ToJsonSchema()["format"]!.GetValue<string>());
        Assert.True(schema.SafeParse(valid).IsSuccess);
        Assert.False(schema.SafeParse(invalid).IsSuccess);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_shortcut_is_the_same_as_Format(string format, Func<StringSchema, StringSchema> shortcut, string valid, string invalid)
    {
        Assert.Equal(Z.String().Format(format).ToJsonSchema().ToJsonString(), shortcut(Z.String()).ToJsonSchema().ToJsonString());
        Assert.Equal(Z.String().Format(format).SafeParse(invalid).IsSuccess, shortcut(Z.String()).SafeParse(invalid).IsSuccess);
        Assert.Equal(Z.String().Format(format).SafeParse(valid).IsSuccess, shortcut(Z.String()).SafeParse(valid).IsSuccess);
    }
}
