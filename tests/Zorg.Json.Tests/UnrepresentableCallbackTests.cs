using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;
using Zorg.Json.Emission;

namespace Zorg.Json.Tests;

public sealed class UnrepresentableCallbackTests
{
    private static JsonSchemaOptions Handling(Func<UnrepresentableContext, JsonObject?> handler) =>
        new() { Unrepresentable = UnrepresentablePolicy.Callback, UnrepresentableHandler = handler };

    [Fact]
    public void The_handler_sees_the_keyword_message_location_and_value()
    {
        List<UnrepresentableContext> seen = [];
        ObjectSchema schema = Z.Object().Property("name", Z.String().Trim().MaxLength(3));

        EmitResult result = schema.Emit(Handling(context =>
        {
            seen.Add(context);
            return null;
        }));

        UnrepresentableContext context = Assert.Single(seen);
        Assert.Equal("maxLength", context.Keyword);
        Assert.Equal("/properties/name", context.Location);
        Assert.Equal(3, context.Value!.GetValue<int>());
        Assert.Contains("Trim", context.Message, StringComparison.Ordinal);
        Assert.Equal(context.Message, Assert.Single(result.Diagnostics).Message);
    }

    [Fact]
    public void A_fragment_is_merged_into_that_subschema()
    {
        ObjectSchema schema = Z.Object()
            .Property("name", Z.String().Trim().MaxLength(3))
            .Property("other", Z.String().MaxLength(3));

        EmitResult result = schema.Emit(Handling(_ => new JsonObject { ["x-after-trim"] = true }));

        JsonObject properties = result.Schema["properties"]!.AsObject();

        Assert.True(properties["name"]!["x-after-trim"]!.GetValue<bool>());
        Assert.False(properties["other"]!.AsObject().ContainsKey("x-after-trim"));
        Assert.False(properties["name"]!.AsObject().ContainsKey("maxLength"));
        Assert.Single(result.Diagnostics);
    }

    [Fact]
    public void A_fragment_cannot_replace_what_was_written()
    {
        EmitResult result = Z.String().MinLength(2).Refine(_ => true, "x")
            .Emit(Handling(_ => new JsonObject { ["minLength"] = 99, ["x-note"] = "kept" }));

        Assert.Equal(2, result.Schema["minLength"]!.GetValue<int>());
        Assert.Equal("kept", result.Schema["x-note"]!.GetValue<string>());
    }

    [Fact]
    public void Null_leaves_the_rule_out_and_nothing_is_annotated()
    {
        EmitResult result = Z.String().Refine(_ => true, "Not reserved").Emit(Handling(_ => null));

        Assert.False(result.Schema.ContainsKey("description"));
        Assert.Equal("Not reserved", Assert.Single(result.Diagnostics).Message.Split('"')[1]);
    }

    [Fact]
    public void A_refinement_reports_its_message_as_the_value()
    {
        UnrepresentableContext? seen = null;

        _ = Z.String().Refine(_ => true, "Not reserved").Emit(Handling(context =>
        {
            seen = context;
            return null;
        }));

        Assert.Null(seen!.Keyword);
        Assert.Equal("Not reserved", seen.Value!.GetValue<string>());
    }

    [Fact]
    public void An_unstated_pattern_reaches_the_handler()
    {
        Regex atomic = new("^(?>a+)b$");

        EmitResult result = Z.String().Pattern(atomic).Emit(Handling(context =>
            context.Keyword == "pattern" ? new JsonObject { ["x-dotnet-pattern"] = context.Value!.DeepClone() } : null));

        Assert.Equal("^(?>a+)b$", result.Schema["x-dotnet-pattern"]!.GetValue<string>());
        Assert.False(result.Schema.ContainsKey("pattern"));
    }

    [Fact]
    public void A_refinement_stays_inexact_whatever_the_handler_returns()
    {
        StringSchema refined = Z.String().Refine(text => text == "a", "x");
        JsonSchemaOptions options = Handling(_ => new JsonObject { ["x-note"] = true });

        JsonObject notEmitted = Z.Not(refined).Emit(options).Schema;
        JsonObject oneOf = Z.OneOf(refined, Z.String().MinLength(5)).Emit(options).Schema;

        Assert.False(notEmitted.ContainsKey("not"));
        Assert.False(oneOf.ContainsKey("oneOf"));
        Assert.True(oneOf.ContainsKey("anyOf"));
    }

    [Fact]
    public void The_handler_is_called_for_the_dropped_keyword_too()
    {
        List<string?> keywords = [];

        _ = Z.Not(Z.String().Refine(_ => true, "x")).Emit(Handling(context =>
        {
            keywords.Add(context.Keyword);
            return null;
        }));

        Assert.Contains("not", keywords);
    }

    [Fact]
    public void Other_policies_ignore_the_handler()
    {
        bool called = false;
        JsonSchemaOptions options = new()
        {
            Unrepresentable = UnrepresentablePolicy.Annotate,
            UnrepresentableHandler = _ =>
            {
                called = true;
                return null;
            },
        };

        _ = Z.String().Refine(_ => true, "x").Emit(options);

        Assert.False(called);
    }

    [Fact]
    public void The_callback_policy_needs_a_handler()
    {
        Assert.Throws<ArgumentException>(() => Z.String().Emit(new() { Unrepresentable = UnrepresentablePolicy.Callback }));
    }

    [Fact]
    public void Handler_exceptions_pass_through()
    {
        Assert.Throws<NotSupportedException>(() => Z.String().Refine(_ => true, "x").Emit(Handling(_ => throw new NotSupportedException())));
    }

    [Fact]
    public void Nested_fragments_stay_with_their_own_subschema()
    {
        ObjectSchema schema = Z.Object()
            .Property("outer", Z.Object().Property("inner", Z.String().Refine(_ => true, "x")).Refine(_ => true, "y"));

        EmitResult result = schema.Emit(Handling(context => new JsonObject { ["x-at"] = context.Location }));

        JsonObject outer = result.Schema["properties"]!["outer"]!.AsObject();

        Assert.Equal("/properties/outer", outer["x-at"]!.GetValue<string>());
        Assert.Equal("/properties/outer/properties/inner", outer["properties"]!["inner"]!["x-at"]!.GetValue<string>());
    }
}
