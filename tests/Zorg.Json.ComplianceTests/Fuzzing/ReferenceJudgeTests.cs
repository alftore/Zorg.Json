using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Xunit;
using Zorg.Json.Emission;
using Zorg.Json.Import;

namespace Zorg.Json.ComplianceTests.Fuzzing;

/// <summary>A judge that can't fail proves nothing, so each one is shown a deliberately broken emitted document here.</summary>
public sealed class ReferenceJudgeTests
{
    private const string Document = """{"$defs":{"s":{"type":"string"}},"properties":{"a":{"$ref":"#/$defs/s"}},"required":["a"]}""";

    private static readonly ReferenceVariant Base = ReferenceVariant.All.First(variant => variant is { Profile: "Draft 2020-12", Name: "base" });

    private static ReferenceCase Case(string document = Document) => ReferenceCase.Build("plain", document, "{}", JsonSchemaImportDialect.Draft202012)!;

    private static List<ReferenceFailure> Judge(Action<JsonObject> breakIt, ReferenceVariant? variant = null, params string[] instances)
    {
        ReferenceCase @case = Case();

        EmitResult Emit(JsonSchemaOptions options)
        {
            EmitResult result = @case.Probe.Emit(options);
            breakIt(result.Schema);
            return result;
        }

        return ReferenceChecker.Check(@case, variant ?? Base, [.. instances.Select(instance => JsonElement.Parse(instance))], Emit);
    }

    private static string[] Kinds(List<ReferenceFailure> failures) => [.. failures.Select(failure => failure.Kind).Distinct()];

    [Fact]
    public void A_sound_document_has_no_failures() => Assert.Empty(Judge(_ => { }, null, """{"a":"x"}""", """{"a":1}"""));

    [Fact]
    public void Judge_a_catches_a_pointer_to_nothing() =>
        Assert.Contains("a-dangling", Kinds(Judge(schema => schema["properties"]!["a"]!["$ref"] = "#/$defs/gone")));

    [Fact]
    public void Judge_a_catches_an_anchor_that_nothing_declares() =>
        Assert.Contains("a-dangling", Kinds(Judge(schema => schema["properties"]!["a"]!["$ref"] = "#gone")));

    [Fact]
    public void Judge_a_catches_a_reference_to_a_list() =>
        Assert.Contains("a-non-schema", Kinds(Judge(schema => schema["properties"]!["a"]!["$ref"] = "#/required")));

    [Fact]
    public void Judge_a_catches_a_reference_to_text_set_aside_as_inert() =>
        Assert.Contains("a-non-schema", Kinds(Judge(schema =>
        {
            schema["x-inert-if"] = new JsonObject();
            schema["properties"]!["a"]!["$ref"] = "#/x-inert-if";
        })));

    [Fact]
    public void Judge_a_catches_a_reference_into_an_unknown_keyword() =>
        Assert.Contains("a-unknown-place", Kinds(Judge(schema =>
        {
            schema["stash"] = new JsonObject();
            schema["properties"]!["a"]!["$ref"] = "#/stash";
        })));

    [Fact]
    public void Judge_a_reads_a_pointer_from_the_resource_it_is_in() =>
        Assert.Contains("a-dangling", Kinds(Judge(schema => schema["properties"]!["a"] = new JsonObject { ["$id"] = "http://example.com/in", ["$ref"] = "#/$defs/s" })));

    [Fact]
    public void Judge_a_catches_two_resources_with_one_id() =>
        Assert.Contains("a-duplicate-id", Kinds(Judge(schema =>
        {
            schema["properties"]!["a"] = new JsonObject { ["$id"] = "http://example.com/in" };
            schema["properties"]!.AsObject()["b"] = new JsonObject { ["$id"] = "http://example.com/in" };
        })));

    [Fact]
    public void Judge_a_catches_two_anchors_with_one_name() =>
        Assert.Contains("a-duplicate-anchor", Kinds(Judge(schema =>
        {
            schema["properties"]!["a"] = new JsonObject { ["$anchor"] = "n" };
            schema["properties"]!.AsObject()["b"] = new JsonObject { ["$anchor"] = "n" };
        })));

    [Fact]
    public void Judge_b_catches_a_document_that_does_not_load() =>
        Assert.Contains("b-does-not-load", Kinds(Judge(schema => schema["type"] = 1)));

    [Fact]
    public void Judge_c_catches_a_schema_stricter_than_parsing() =>
        Assert.Contains("c-stricter-than-parsing", Kinds(Judge(schema => schema["$defs"]!["s"]!["type"] = "number", null, """{"a":"x"}""")));

    [Fact]
    public void Judge_d_catches_a_crash()
    {
        List<ReferenceFailure> failures = ReferenceChecker.Check(Case(), Base, [], _ => throw new FormatException());

        Assert.Equal("d-emit-crashed", Assert.Single(failures).Kind);
    }

    [Fact]
    public void Judge_d_catches_a_hang()
    {
        using ManualResetEventSlim released = new();

        try
        {
            List<ReferenceFailure> failures = ReferenceChecker.Check(
                Case(),
                Base,
                [],
                _ =>
                {
                    released.Wait();
                    throw new InvalidOperationException();
                },
                timeout: TimeSpan.FromMilliseconds(50));

            Assert.Equal("d-emit-hung", Assert.Single(failures).Kind);
        }
        finally
        {
            released.Set();
        }
    }

    [Fact]
    public void Judge_d_lets_a_stated_limit_through()
    {
        ReferenceCounters counters = new();

        Assert.Empty(ReferenceChecker.Check(Case(), Base, [], _ => throw new InvalidOperationException("Too deep"), counters));
        Assert.Equal(1, counters["stated limit: Too deep"]);
    }

    [Fact]
    public void Judge_e_catches_a_reference_aimed_at_the_wrong_place() =>
        Assert.Contains("e-looser-with-no-diagnostic", Kinds(Judge(schema => schema["$defs"]!["s"]!.AsObject().Remove("type"), null, """{"a":1}""")));

    [Fact]
    public void The_shape_of_a_document_ignores_names_and_indexes() =>
        Assert.Equal(
            ReferenceShrinker.Shape("""{"$defs":{"x":{"$ref":"#/allOf/1/properties/q"}},"allOf":[{},{}]}"""),
            ReferenceShrinker.Shape("""{"$defs":{"y":{"$ref":"#/allOf/0/properties/r"}},"allOf":[{},{}]}"""));

    [Fact]
    public void A_failure_shrinks_to_what_causes_it()
    {
        const string Noisy = """{"title":"t","properties":{"p":{"type":"string"},"q":{"not":{"format":"date","$anchor":"gone"}}},"$defs":{"d":{"$ref":"#gone"}}}""";
        ReferenceCase @case = Case(Noisy);
        ReferenceFailure failure = ReferenceChecker.Check(@case, Base, []).First(found => found.Kind == "a-dangling");

        (string shrunk, string _) = ReferenceShrinker.Shrink(failure, @case, Base);

        Assert.DoesNotContain("title", shrunk, StringComparison.Ordinal);
        Assert.DoesNotContain("\"p\"", shrunk, StringComparison.Ordinal);
        Assert.Contains("gone", shrunk, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_profile_has_every_variant() =>
        Assert.Equal(4, ReferenceVariant.All.GroupBy(variant => variant.Profile).Count(group => group.Select(variant => variant.Name).Distinct().Count() == 7));
}
