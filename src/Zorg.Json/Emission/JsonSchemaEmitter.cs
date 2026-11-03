using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Zorg.Json.Model;

namespace Zorg.Json.Emission;

/// <summary>Writes a <see cref="SchemaNode"/> as JSON Schema, with keywords in a fixed order.</summary>
internal static class JsonSchemaEmitter
{
    public static EmitResult Emit(SchemaNode node, JsonSchemaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.Target);

        ImmutableArray<EmitDiagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<EmitDiagnostic>();
        List<string> notes = [];

        // Normalization runs before constraints, so a constraint it can affect may reject
        // input that parsing accepts. The input schema must never be stricter than parsing.
        bool relocate = options.Direction == SchemaDirection.Input && !node.Behavior.Normalizers.IsEmpty;
        SchemaNode emitted = relocate ? Relocate(node, diagnostics, notes) : node;

        foreach (SchemaRefinement refinement in node.Behavior.Refinements)
        {
            notes.Add($"Refinement: {refinement.Message}");
            diagnostics.Add(new EmitDiagnostic(null, $"The refinement \"{refinement.Message}\" can't be expressed in JSON Schema."));
        }

        if (diagnostics.Count > 0 && options.Unrepresentable == UnrepresentablePolicy.Throw)
        {
            throw new InvalidOperationException(string.Join(' ', diagnostics.Select(diagnostic => diagnostic.Message)));
        }

        string? description = options.Unrepresentable == UnrepresentablePolicy.Annotate
            ? Describe(node.Description, notes)
            : node.Description;

        JsonObject schema = new() { ["$schema"] = options.Target.Dialect };
        Add(schema, "title", emitted.Title);
        Add(schema, "description", description);
        AddType(schema, emitted.Types);

        if (emitted.Const is { } constant)
        {
            schema["const"] = JsonValue.Create(constant);
        }

        if (!emitted.Enum.IsEmpty)
        {
            schema["enum"] = new JsonArray([.. emitted.Enum.Select(value => (JsonNode?)JsonValue.Create(value))]);
        }

        Add(schema, "minLength", emitted.MinLength);
        Add(schema, "maxLength", emitted.MaxLength);
        Add(schema, "pattern", emitted.Pattern);
        Add(schema, "format", emitted.Format);

        return new EmitResult(schema, diagnostics.ToImmutable());
    }

    private static SchemaNode Relocate(SchemaNode node, ImmutableArray<EmitDiagnostic>.Builder diagnostics, List<string> notes)
    {
        string steps = string.Join(", ", node.Behavior.Normalizers.Select(normalizer => normalizer.Name));
        List<string> relocated = [];

        void Note(string keyword, string value)
        {
            relocated.Add($"{keyword} {value}");
            diagnostics.Add(new EmitDiagnostic(keyword, $"{keyword} applies after {steps}, so the input schema can't state it."));
        }

        if (node.Const is { } constant)
        {
            Note("const", constant.GetRawText());
        }

        if (!node.Enum.IsEmpty)
        {
            Note("enum", $"[{string.Join(", ", node.Enum.Select(value => value.GetRawText()))}]");
        }

        if (node.MinLength is { } minLength)
        {
            Note("minLength", minLength.ToString(CultureInfo.InvariantCulture));
        }

        if (node.MaxLength is { } maxLength)
        {
            Note("maxLength", maxLength.ToString(CultureInfo.InvariantCulture));
        }

        if (node.Pattern is { } pattern)
        {
            Note("pattern", pattern);
        }

        if (node.Format is { } format)
        {
            Note("format", format);
        }

        if (relocated.Count > 0)
        {
            notes.Insert(0, $"After {steps}: {string.Join("; ", relocated)}.");
        }

        return node with { Const = null, Enum = [], MinLength = null, MaxLength = null, Pattern = null, Format = null };
    }

    private static string? Describe(string? description, List<string> notes)
    {
        if (notes.Count == 0)
        {
            return description;
        }

        string text = string.Join(' ', notes);
        return description is null ? text : $"{description} {text}";
    }

    private static void AddType(JsonObject schema, JsonTypes types)
    {
        if (types == JsonTypes.String)
        {
            schema["type"] = "string";
        }
    }

    private static void Add(JsonObject schema, string keyword, string? value)
    {
        if (value is not null)
        {
            schema[keyword] = value;
        }
    }

    private static void Add(JsonObject schema, string keyword, int? value)
    {
        if (value is not null)
        {
            schema[keyword] = value;
        }
    }
}
