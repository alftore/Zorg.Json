using System;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Zorg.Json.Model;

namespace Zorg.Json.Validation;

/// <summary>A <see cref="SchemaNode"/> prepared for repeated validation.</summary>
internal sealed class ValidationPlan
{
    private readonly SchemaNode _node;
    private readonly Regex? _pattern;
    private readonly Func<string, bool>? _format;

    public ValidationPlan(SchemaNode node)
    {
        _node = node;
        _pattern = node.Pattern is null ? null : PatternCompiler.Compile(node.Pattern);
        _format = StringFormats.Find(node.Format);
    }

    /// <summary>Runs the pipeline and returns the value after any normalization.</summary>
    public JsonElement Run(JsonElement value, ValidationContext context)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            context.Report(IssueCodes.InvalidType, "type", IssueCodes.InvalidType, "expected", "string", "string", TypeName(value.ValueKind));
            return value;
        }

        string original;

        try
        {
            original = value.GetString()!;
        }
        catch (InvalidOperationException)
        {
            context.Report(IssueCodes.InvalidValue, "type", "invalid_value.string", "expected", "string");
            return value;
        }

        string text = original;

        foreach (SchemaNormalizer normalizer in _node.Behavior.Normalizers)
        {
            text = normalizer.Apply(text);
        }

        CheckConstraints(text, context);
        return text == original ? value : JsonStrings.ToElement(text);
    }

    private static string TypeName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        _ => "null",
    };

    private void CheckConstraints(string text, ValidationContext context)
    {
        if (_node.MinLength is not null || _node.MaxLength is not null)
        {
            int length = JsonStrings.CodePointCount(text);

            if (length < _node.MinLength)
            {
                context.Report(IssueCodes.TooSmall, "minLength", "too_small.string", "minimum", _node.MinLength, _node.MinLength);
            }

            if (length > _node.MaxLength)
            {
                context.Report(IssueCodes.TooBig, "maxLength", "too_big.string", "maximum", _node.MaxLength, _node.MaxLength);
            }
        }

        if (_pattern is not null && !_pattern.IsMatch(text))
        {
            context.Report(IssueCodes.InvalidFormat, "pattern", "invalid_format.pattern", "pattern", _node.Pattern, _node.Pattern);
        }

        if (_format is not null && !_format(text))
        {
            context.Report(IssueCodes.InvalidFormat, "format", "invalid_format.format", "format", _node.Format, _node.Format);
        }

        if (_node.Const is { } constant && !constant.ValueEquals(text))
        {
            context.Report(IssueCodes.InvalidValue, "const", "invalid_value.const", "expected", constant.GetString(), constant.GetRawText());
        }

        if (!_node.Enum.IsEmpty && !_node.Enum.Any(allowed => allowed.ValueEquals(text)))
        {
            string[] values = [.. _node.Enum.Select(allowed => allowed.GetString()!)];
            string list = string.Join(", ", _node.Enum.Select(allowed => allowed.GetRawText()));
            context.Report(IssueCodes.InvalidValue, "enum", "invalid_value.enum", "values", values, list);
        }
    }
}
