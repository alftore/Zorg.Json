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
        const string Noisy = """{"title":"t","properties":{"p":{"type":"string"},"q":{"type":"string"}},"$defs":{"gone":{}}}""";

        // Broken for as long as the definition is in the document.
        static EmitResult Broken(ReferenceCase @case, JsonSchemaOptions options)
        {
            EmitResult result = @case.Probe.Emit(options);

            if (@case.Document.Contains("gone", StringComparison.Ordinal))
            {
                result.Schema["$ref"] = "#/$defs/nowhere";
            }

            return result;
        }

        ReferenceCase @case = Case(Noisy);
        ReferenceFailure failure = ReferenceChecker.Check(@case, Base, [], options => Broken(@case, options)).First(found => found.Kind == "a-dangling");

        (string shrunk, string _) = ReferenceShrinker.Shrink(failure, @case, Base, Broken);

        Assert.DoesNotContain("title", shrunk, StringComparison.Ordinal);
        Assert.DoesNotContain("\"p\"", shrunk, StringComparison.Ordinal);
        Assert.Contains("gone", shrunk, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"$schema":"https://json-schema.org/draft/2020-12/schema","items":{"allOf":[{"$ref":"#/items/allOf/1/items"},{"items":{"$anchor":"a"}}]}}""")]
    [InlineData("""{"$schema":"https://json-schema.org/draft/2020-12/schema","properties":{"x":{"prefixItems":[{"if":{"$ref":"#/properties/x/not"}}],"not":{"allOf":[{"$id":"a.json"}]}},"z":{"prefixItems":[{"if":{"$ref":"#/properties/z/not"}}],"not":{"allOf":[{"$id":"a.json-2"}]}}}}""")]
    public void JsonSchema_Net_refuses_a_sound_document(string document)
    {
        // Not one word of these is Zorg's, which is why the report keeps its refusals of sound documents apart.
        Exception? refused = Record.Exception(() => global::Json.Schema.JsonSchema.FromText(document, new global::Json.Schema.BuildOptions { SchemaRegistry = new global::Json.Schema.SchemaRegistry() }));

        Assert.NotNull(refused);
        Assert.Empty(ReferenceGraph.Check((JsonObject)JsonNode.Parse(document)!, JsonSchemaDialect.Draft202012));
    }

    [Fact]
    public void Judge_g_catches_a_stricter_document_that_the_oracle_gave_up_on()
    {
        const string Refused = """{"$schema":"https://json-schema.org/draft/2020-12/schema","items":{"allOf":[{"$ref":"#/items/allOf/1/items"},{"items":{"$anchor":"a"}}]}}""";
        ReferenceCase @case = ReferenceCase.Build("plain", Refused, "{}", JsonSchemaImportDialect.Draft202012)!;

        EmitResult Stricter(JsonSchemaOptions options)
        {
            EmitResult result = @case.Probe.Emit(options);
            result.Schema["type"] = "string";
            return result;
        }

        List<ReferenceFailure> failures = ReferenceChecker.Check(@case, Base, [JsonElement.Parse("1")], Stricter);

        Assert.Contains(ReferenceChecker.OracleLimit, Kinds(failures));
        Assert.Contains("g-stricter-when-read-back", Kinds(failures));
    }

    [Fact]
    public void Each_profile_has_every_variant() =>
        Assert.Equal(4, ReferenceVariant.All.GroupBy(variant => variant.Profile).Count(group => group.Select(variant => variant.Name).Distinct().Count() == 8));

    [Fact]
    public void Judge_d_catches_an_emit_over_its_budget()
    {
        ReferenceCase @case = Case();

        List<ReferenceFailure> failures = ReferenceChecker.Check(@case, Base, [], @case.Probe.Emit, budget: TimeSpan.Zero);

        Assert.Contains("d-emit-slow", Kinds(failures));
    }

    [Fact]
    public void Judge_h_catches_a_made_up_name_with_two_meanings()
    {
        JsonSchemaOptions options = new() { ReferencePrefix = "#/components/schemas/" };
        JsonValueSchema schema = Z.FromJsonSchema("""{"type":"object","properties":{"k":{"$ref":"#"}}}""");
        EmitResult first = schema.Emit(options);
        EmitResult second = schema.Emit(options);
        Assert.Empty(ReferenceChecker.CheckComponents([first, second]));

        Assert.Single(second.Definitions).Value["title"] = "changed";

        Assert.Contains(ReferenceChecker.CheckComponents([first, second]), found => found.Kind == "h-components-clash");
    }

    [Fact]
    public void Judge_h_catches_a_reference_that_leaves_the_components()
    {
        JsonSchemaOptions options = new() { ReferencePrefix = "#/components/schemas/" };
        EmitResult result = Z.FromJsonSchema("""{"$defs":{"a":{}},"properties":{"k":{"$ref":"#/$defs/a"}}}""").Emit(options);

        result.Schema["properties"]!["k"]!["$ref"] = "#/components/schemas/gone";

        Assert.Contains(ReferenceChecker.CheckComponents([result]), found => found.Kind == "a-dangling");
    }
}
