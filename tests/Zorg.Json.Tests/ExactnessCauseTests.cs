using System;
using System.Linq;
using Xunit;
using Zorg.Json.Emission;

namespace Zorg.Json.Tests;

public sealed class ExactnessCauseTests
{
    private static EmitDiagnostic Single(EmitResult result, string keyword) =>
        Assert.Single(result.Diagnostics, diagnostic => diagnostic.Keyword == keyword);

    [Fact]
    public void A_relaxed_oneOf_names_the_rule_and_the_branch()
    {
        EmitDiagnostic diagnostic = Single(Z.OneOf(Z.String().Email(), Z.String().Uuid()).Emit(), "oneOf");

        Assert.Contains("format at /oneOf/0", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_location_includes_where_the_keyword_sits()
    {
        ObjectSchema schema = Z.Object().Property("item", Z.OneOf(Z.String(), Z.String().Uuid()));

        EmitDiagnostic diagnostic = Single(schema.Emit(), "oneOf");

        Assert.Equal("/properties/item", diagnostic.Location);
        Assert.Contains("format at /properties/item/oneOf/1", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dropped_not_names_a_rule_inside_it()
    {
        JsonValueSchema schema = Z.Not(Z.Object().Property("id", Z.String().Uuid()));

        Assert.Contains("format at /not/properties/id", Single(schema.Emit(), "not").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dropped_if_names_a_rule_inside_it()
    {
        JsonValueSchema schema = Z.If(Z.String().Trim(), Z.String());

        Assert.Contains("Trim at /if", Single(schema.Emit(), "if").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dropped_maxContains_names_a_rule_inside_contains()
    {
        EmitDiagnostic diagnostic = Single(Z.Array().Contains(Z.String().Trim(), maximum: 1).Emit(), "maxContains");

        Assert.Contains("Trim at /contains", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_rule_is_found_through_a_reference()
    {
        SchemaBox box = new();
        box.Value = Z.Object().Property("next", Z.Lazy("node", () => box.Value!).Optional()).Property("id", Z.String().Uuid());
        JsonValueSchema schema = Z.Not(box.Value);

        EmitDiagnostic diagnostic = Single(schema.Emit(), "not");

        Assert.Contains("format at /not/properties/id", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_throw_policy_names_the_rule()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => Z.Not(Z.String().Uuid()).Emit(new JsonSchemaOptions { Unrepresentable = UnrepresentablePolicy.Throw }));

        Assert.Contains("format at /not", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Exact_schemas_report_nothing()
    {
        Assert.Empty(Z.Not(Z.String().MinLength(1)).Emit().Diagnostics);
        Assert.Empty(Z.OneOf(Z.String(), Z.Integer()).Emit().Diagnostics);
    }

    [Fact]
    public void The_first_inexact_branch_is_named()
    {
        EmitDiagnostic diagnostic = Single(Z.OneOf(Z.String().MinLength(1), Z.String().Date(), Z.String().Email()).Emit(), "oneOf");

        Assert.Contains("format at /oneOf/1", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("/oneOf/2", diagnostic.Message, StringComparison.Ordinal);
        Assert.Single(Z.OneOf(Z.String().Date(), Z.String().Email()).Emit().Diagnostics.Where(item => item.Keyword == "oneOf"));
    }

    private sealed class SchemaBox
    {
        public ObjectSchema? Value { get; set; }
    }
}
