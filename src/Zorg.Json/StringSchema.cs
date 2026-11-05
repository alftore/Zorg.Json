using System;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Zorg.Json.Model;
using Zorg.Json.Validation;

namespace Zorg.Json;

/// <summary>A schema for JSON strings. Create one with <see cref="Z.String"/>. Zod equivalent: <c>ZodString</c>.</summary>
public sealed class StringSchema : Schema<string, StringSchema>
{
    internal StringSchema(SchemaNode node)
        : base(node)
    {
    }

    /// <summary>
    /// Requires at least this many Unicode code points, so an emoji counts as one. JSON Schema: <c>minLength</c>.
    /// Zod equivalent: <c>.min()</c>.
    /// </summary>
    /// <param name="length">The minimum length.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// StringSchema schema = Z.String().MinLength(1);
    /// </code>
    /// </example>
    [Pure]
    public StringSchema MinLength(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return With(Node with { MinLength = length });
    }

    /// <summary>
    /// Allows at most this many Unicode code points, so an emoji counts as one. JSON Schema: <c>maxLength</c>.
    /// Zod equivalent: <c>.max()</c>.
    /// </summary>
    /// <param name="length">The maximum length.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// StringSchema schema = Z.String().MaxLength(100);
    /// </code>
    /// </example>
    [Pure]
    public StringSchema MaxLength(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return With(Node with { MaxLength = length });
    }

    /// <summary>
    /// Requires a regular expression match, which isn't anchored unless the pattern says so.
    /// JSON Schema: <c>pattern</c>. Zod equivalent: <c>.regex()</c>.
    /// </summary>
    /// <param name="pattern">The regular expression.</param>
    /// <returns>A new schema.</returns>
    /// <exception cref="ArgumentException">The pattern isn't a valid regular expression.</exception>
    /// <example>
    /// <code>
    /// StringSchema schema = Z.String().Pattern("^[a-z]+$");
    /// </code>
    /// </example>
    [Pure]
    public StringSchema Pattern([StringSyntax(StringSyntaxAttribute.Regex)] string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        _ = PatternCompiler.Compile(pattern);
        return With(Node with { Pattern = pattern, Behavior = Node.Behavior with { PatternRegex = null, PatternIsUnstated = false } });
    }

    /// <summary>
    /// Requires a match of a regular expression you built, such as a <c>[GeneratedRegex]</c> one. Validation uses the
    /// <see cref="Regex"/> as given, with its options and timeout. JSON Schema: <c>pattern</c>. Zod equivalent: <c>.regex()</c>.
    /// </summary>
    /// <param name="regex">The expression, matched unanchored unless it says otherwise.</param>
    /// <param name="ecmaPattern">
    /// The same rule in ECMA-262 syntax, written as <c>pattern</c>. It must not match fewer strings than
    /// <paramref name="regex"/> does, or the emitted schema would reject what parsing accepts. Without it, <c>pattern</c>
    /// is left out: a .NET regex can differ from its ECMA-262 reading even when the text is valid in both (<c>$</c> matches
    /// before a final newline, and <c>\d</c> matches every Unicode digit), so the text is never taken as the pattern.
    /// </param>
    /// <returns>A new schema.</returns>
    /// <exception cref="ArgumentException"><paramref name="ecmaPattern"/> isn't a valid ECMA-262 pattern.</exception>
    /// <remarks>
    /// Without <paramref name="ecmaPattern"/> the rule is unrepresentable: <c>pattern</c> is left out, with a diagnostic (see
    /// <see cref="Emission.JsonSchemaOptions.Unrepresentable"/>). The schema stays looser, which is safe, but the node is also
    /// treated as inexact: a .NET regex can differ from its ECMA-262 text, so a subschema with a given
    /// <see cref="Regex"/> is never written under <c>not</c>, <c>if</c> or <c>oneOf</c>.
    /// </remarks>
    /// <example>
    /// <code>
    /// [GeneratedRegex("^[a-z]+$")]
    /// private static partial Regex Lower();
    ///
    /// StringSchema schema = Z.String().Pattern(Lower(), "^[a-z]+$");
    /// </code>
    /// </example>
    [Pure]
    public StringSchema Pattern(Regex regex, string? ecmaPattern = null)
    {
        ArgumentNullException.ThrowIfNull(regex);

        if (ecmaPattern is not null)
        {
            _ = PatternCompiler.Compile(ecmaPattern);
        }

        return With(Node with
        {
            Pattern = ecmaPattern,
            Behavior = Node.Behavior with { PatternRegex = regex, PatternIsUnstated = ecmaPattern is null },
        });
    }

    /// <summary>
    /// Sets the format. <c>email</c>, <c>uuid</c>, <c>uri</c>, <c>date-time</c> and <c>date</c> are enforced;
    /// any other name is only emitted. JSON Schema: <c>format</c>.
    /// </summary>
    /// <param name="format">The format name.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// StringSchema schema = Z.String().Format("hostname");
    /// </code>
    /// </example>
    [Pure]
    public StringSchema Format(string format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return With(Node with { Format = format, Behavior = Node.Behavior with { FormatIsFluent = true } });
    }

    /// <summary>Requires an email address. JSON Schema: <c>format: email</c>. Zod equivalent: <c>z.email()</c>.</summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// StringSchema schema = Z.String().Email();
    /// </code>
    /// </example>
    [Pure]
    public StringSchema Email() => Format("email");

    /// <summary>Requires a UUID. JSON Schema: <c>format: uuid</c>. Zod equivalent: <c>z.uuid()</c>.</summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// StringSchema schema = Z.String().Uuid();
    /// </code>
    /// </example>
    [Pure]
    public StringSchema Uuid() => Format("uuid");

    /// <summary>Requires an absolute URI. JSON Schema: <c>format: uri</c>. Zod equivalent: <c>z.url()</c>.</summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// StringSchema schema = Z.String().Uri();
    /// </code>
    /// </example>
    [Pure]
    public StringSchema Uri() => Format("uri");

    /// <summary>Requires an RFC 3339 timestamp. JSON Schema: <c>format: date-time</c>. Zod equivalent: <c>z.iso.datetime()</c>.</summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// StringSchema schema = Z.String().DateTime();
    /// </code>
    /// </example>
    [Pure]
    public StringSchema DateTime() => Format("date-time");

    /// <summary>Requires an RFC 3339 date. JSON Schema: <c>format: date</c>. Zod equivalent: <c>z.iso.date()</c>.</summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// StringSchema schema = Z.String().Date();
    /// </code>
    /// </example>
    [Pure]
    public StringSchema Date() => Format("date");

    /// <summary>Requires exactly this value. JSON Schema: <c>const</c>. Zod equivalent: <c>z.literal()</c>.</summary>
    /// <param name="value">The only allowed value.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// StringSchema schema = Z.String().Const("circle");
    /// </code>
    /// </example>
    [Pure]
    public StringSchema Const(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        SchemaNode node = Node with { Const = JsonStrings.ToElement(value) };
        return With(Node.Types.HasFlag(JsonTypes.Null) ? node.WithNull() : node);
    }

    /// <summary>Requires one of these values. JSON Schema: <c>enum</c>. Zod equivalent: <c>z.enum()</c>.</summary>
    /// <param name="values">The allowed values.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// StringSchema schema = Z.String().Enum("web", "news");
    /// </code>
    /// </example>
    [Pure]
    public StringSchema Enum(params ReadOnlySpan<string> values)
    {
        ImmutableArray<JsonElement>.Builder elements = ImmutableArray.CreateBuilder<JsonElement>(values.Length);

        foreach (string value in values)
        {
            ArgumentNullException.ThrowIfNull(value);
            elements.Add(JsonStrings.ToElement(value));
        }

        SchemaNode node = Node with { Enum = elements.MoveToImmutable() };
        return With(Node.Types.HasFlag(JsonTypes.Null) ? node.WithNull() : node);
    }

    /// <summary>
    /// Also allows JSON <c>null</c>, which parses to a null string. JSON Schema: adds <c>null</c> to <c>type</c>.
    /// Zod equivalent: <c>.nullable()</c>.
    /// </summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// StringSchema schema = Z.String().Nullable();
    /// </code>
    /// </example>
    [Pure]
    public StringSchema Nullable() => With(Node.WithNull());

    /// <summary>
    /// Removes surrounding whitespace before the constraints run. No keyword: constraints that it can affect are left
    /// out of the input schema. Zod equivalent: <c>.trim()</c>.
    /// </summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// StringSchema schema = Z.String().Trim().MinLength(1);
    /// </code>
    /// </example>
    [Pure]
    public StringSchema Trim() => Normalize(nameof(Trim), static text => text.Trim());

    /// <summary>
    /// Removes leading whitespace before the constraints run. No keyword: constraints that it can affect are left
    /// out of the input schema.
    /// </summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// StringSchema schema = Z.String().TrimStart();
    /// </code>
    /// </example>
    [Pure]
    public StringSchema TrimStart() => Normalize(nameof(TrimStart), static text => text.TrimStart());

    /// <summary>
    /// Removes trailing whitespace before the constraints run. No keyword: constraints that it can affect are left
    /// out of the input schema.
    /// </summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// StringSchema schema = Z.String().TrimEnd();
    /// </code>
    /// </example>
    [Pure]
    public StringSchema TrimEnd() => Normalize(nameof(TrimEnd), static text => text.TrimEnd());

    /// <summary>
    /// Converts to lower case before the constraints run. No keyword: constraints that it can affect are left out of
    /// the input schema. Zod equivalent: <c>.toLowerCase()</c>.
    /// </summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// StringSchema schema = Z.String().ToLowerInvariant().Enum("web", "news");
    /// </code>
    /// </example>
    [Pure]
    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Lower case is what the caller asked for.")]
    public StringSchema ToLowerInvariant() => Normalize(nameof(ToLowerInvariant), static text => text.ToLowerInvariant());

    /// <summary>
    /// Converts to upper case before the constraints run. No keyword: constraints that it can affect are left out of
    /// the input schema. Zod equivalent: <c>.toUpperCase()</c>.
    /// </summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// StringSchema schema = Z.String().ToUpperInvariant();
    /// </code>
    /// </example>
    [Pure]
    public StringSchema ToUpperInvariant() => Normalize(nameof(ToUpperInvariant), static text => text.ToUpperInvariant());

    /// <summary>
    /// Applies Unicode normalization before the constraints run. No keyword: constraints that it can affect are left out
    /// of the input schema. Zod equivalent: <c>.normalize()</c>.
    /// </summary>
    /// <param name="form">The normalization form.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// StringSchema schema = Z.String().NormalizeUnicode();
    /// </code>
    /// </example>
    [Pure]
    public StringSchema NormalizeUnicode(NormalizationForm form = NormalizationForm.FormC) =>
        Normalize(nameof(NormalizeUnicode), text => NormalizeOrKeep(text, form));

    /// <summary>
    /// Replaces the value before the constraints run, keeping it a string. No keyword: constraints that it can affect
    /// are left out of the input schema. Zod equivalent: <c>.overwrite()</c>.
    /// </summary>
    /// <param name="overwrite">The replacement.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// StringSchema schema = Z.String().Overwrite(text => text.Replace("-", ""));
    /// </code>
    /// </example>
    [Pure]
    public StringSchema Overwrite(Func<string, string> overwrite)
    {
        ArgumentNullException.ThrowIfNull(overwrite);
        return Normalize(nameof(Overwrite), overwrite);
    }

    private protected override StringSchema With(SchemaNode node) => new(node);

    internal override string Materialize(JsonElement value) => value.GetString()!;

    internal override JsonElement ToElement(string value) => JsonStrings.ToElement(value);

    /// <summary>Unicode noncharacters are valid in JSON, but <see cref="string.Normalize(NormalizationForm)"/> can refuse them.</summary>
    private static string NormalizeOrKeep(string text, NormalizationForm form)
    {
        try
        {
            return text.Normalize(form);
        }
        catch (ArgumentException)
        {
            return text;
        }
    }

    private StringSchema Normalize(string name, Func<string, string> apply) => With(Node with
    {
        Behavior = Node.Behavior with { Normalizers = Node.Behavior.Normalizers.Add(new SchemaNormalizer(name, apply)) },
    });
}
