using System;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Zorg.Json.Emission;

namespace Zorg.Json.Tests;

public sealed class StringSchemaEmitTests
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    private static string Json(JsonObject schema) => schema.ToJsonString(Indented);

    [Fact]
    public void Plain_string() => Snapshot.Match(Json(Z.String().ToJsonSchema()));

    [Fact]
    public void All_keywords_in_canonical_order() =>
        Snapshot.Match(Json(Z.String()
            .Format("email")
            .Pattern("^a")
            .MaxLength(5)
            .MinLength(1)
            .Enum("ab", "ac")
            .Const("ab")
            .Describe("A description")
            .Title("A title")
            .ToJsonSchema()));

    [Fact]
    public void Draft07_target() =>
        Snapshot.Match(Json(Z.String().MinLength(1).ToJsonSchema(new() { Target = JsonSchemaProfile.Draft07 })));

    [Fact]
    public void Input_leaves_out_constraints_after_normalization() =>
        Snapshot.Match(Json(Z.String().Trim().ToLowerInvariant().MinLength(1).MaxLength(3).Pattern("^a").Email().Enum("a").Const("a").Describe("Name").ToJsonSchema()));

    [Fact]
    public void Output_keeps_constraints_after_normalization() =>
        Snapshot.Match(Json(Z.String().Trim().MaxLength(3).ToJsonSchema(new() { Direction = SchemaDirection.Output })));

    [Fact]
    public void Normalization_without_constraints_adds_no_note()
    {
        EmitResult result = Z.String().Trim().Emit();

        Assert.False(result.Schema.ContainsKey("description"));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Refinement_is_annotated_by_default()
    {
        EmitResult result = Z.String().Refine(_ => true, "Not reserved").Emit();

        Assert.Equal("Refinement: Not reserved", result.Schema["description"]!.GetValue<string>());
        Assert.Null(Assert.Single(result.Diagnostics).Keyword);
    }

    [Fact]
    public void Omit_policy_keeps_the_diagnostic_but_not_the_note()
    {
        EmitResult result = Z.String().Trim().MaxLength(3).Refine(_ => true, "x").Emit(new() { Unrepresentable = UnrepresentablePolicy.Omit });

        Assert.False(result.Schema.ContainsKey("description"));
        Assert.False(result.Schema.ContainsKey("maxLength"));
        Assert.Equal(2, result.Diagnostics.Length);
    }

    [Fact]
    public void Throw_policy_throws_when_something_is_left_out()
    {
        JsonSchemaOptions options = new() { Unrepresentable = UnrepresentablePolicy.Throw };

        Assert.Throws<InvalidOperationException>(() => Z.String().Refine(_ => true, "x").ToJsonSchema(options));
        Assert.Throws<InvalidOperationException>(() => Z.String().Trim().MaxLength(3).ToJsonSchema(options));
        Assert.Equal("string", Z.String().MaxLength(3).ToJsonSchema(options)["type"]!.GetValue<string>());
    }

    [Fact]
    public void Each_call_returns_a_new_document()
    {
        StringSchema schema = Z.String();
        JsonObject first = schema.ToJsonSchema();

        first["title"] = "changed";

        Assert.False(schema.ToJsonSchema().ContainsKey("title"));
    }
}
