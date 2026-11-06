using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Json.Schema;
using Xunit;
using Zorg.Json.Emission;
using Zorg.Json.Import;

namespace Zorg.Json.ComplianceTests;

/// <summary>
/// Cases from a code review of emission. Each emitted schema is judged by JsonSchema.Net, so none of them depends on
/// Zorg's own reading of what it wrote.
/// </summary>
public sealed class EmissionReviewTests
{
    private static readonly JsonSchemaProfile[] Profiles =
        [JsonSchemaProfile.Draft202012, JsonSchemaProfile.Draft07, JsonSchemaProfile.OpenApi31, JsonSchemaProfile.OpenApi30];

    private static JsonValueSchema Import(string document, JsonSchemaImportOptions? options = null) => Z.FromJsonSchema(document, options);

    /// <summary>The Schema Object of OpenAPI 3.0, translated into 2020-12 for the oracle.</summary>
    internal static JsonNode? ConvertOpenApi30(JsonNode? node)
    {
        if (node is JsonArray list)
        {
            return new JsonArray([.. list.Select(ConvertOpenApi30)]);
        }

        if (node is not JsonObject schema)
        {
            return node?.DeepClone();
        }

        JsonObject result = [];

        foreach ((string keyword, JsonNode? value) in schema)
        {
            if (keyword is "properties" or "$defs" or "definitions")
            {
                JsonObject named = [];

                foreach ((string name, JsonNode? child) in (JsonObject)value!)
                {
                    named[name] = ConvertOpenApi30(child);
                }

                result[keyword] = named;
            }
            else if (keyword is "items" or "not" or "additionalProperties" or "allOf" or "anyOf" or "oneOf")
            {
                result[keyword] = ConvertOpenApi30(value);
            }
            else if (keyword is not ("nullable" or "exclusiveMinimum" or "exclusiveMaximum" or "minimum" or "maximum" or "type"))
            {
                result[keyword] = value?.DeepClone();
            }
        }

        bool nullable = schema["nullable"] is JsonValue flag && flag.GetValue<bool>();

        if (schema["type"] is { } type)
        {
            result["type"] = nullable ? new JsonArray(type.DeepClone(), "null") : type.DeepClone();
        }

        void Limit(string inclusive, string exclusive)
        {
            if (schema[inclusive] is { } limit)
            {
                result[schema[exclusive] is JsonValue on && on.GetValue<bool>() ? exclusive : inclusive] = limit.DeepClone();
            }
        }

        Limit("minimum", "exclusiveMinimum");
        Limit("maximum", "exclusiveMaximum");
        return result;
    }

    private static bool Accepts(string schemaText, JsonSchemaProfile profile, string instance)
    {
        JsonNode node = JsonNode.Parse(schemaText)!;

        if (profile.Dialect == JsonSchemaDialect.OpenApi30)
        {
            node = ConvertOpenApi30(node)!;
        }

        RemoveInert(node);

        if (node is JsonObject root && !root.ContainsKey("$schema"))
        {
            root["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        }

        JsonSchema oracle = JsonSchema.FromText(node.ToJsonString(), new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        return oracle.Evaluate(JsonElement.Parse(instance)).IsValid;
    }

    private static void RemoveInert(JsonNode? node)
    {
        if (node is JsonObject schema)
        {
            foreach (string keyword in schema.Select(entry => entry.Key).Where(key => key.StartsWith("x-inert-", StringComparison.Ordinal)).ToArray())
            {
                schema.Remove(keyword);
            }

            foreach ((string _, JsonNode? child) in schema.ToArray())
            {
                RemoveInert(child);
            }
        }
        else if (node is JsonArray list)
        {
            foreach (JsonNode? child in list)
            {
                RemoveInert(child);
            }
        }
    }

    /// <summary>The emitted schema accepts every instance that parsing accepts, in each of the profiles.</summary>
    private static void AssertNeverStricter<T>(string name, Schema<T> schema, IEnumerable<JsonSchemaProfile> profiles, params string[] instances)
    {
        foreach (JsonSchemaProfile profile in profiles)
        {
            string text = schema.Emit(new JsonSchemaOptions { Target = profile }).Schema.ToJsonString();

            foreach (string instance in instances.Where(instance => schema.SafeParse(instance).IsSuccess))
            {
                Assert.True(Accepts(text, profile, instance), $"{name} in {profile.Name} is stricter than parsing on {instance}: {text}");
            }
        }
    }

    private static void AssertNeverStricter<T>(string name, Schema<T> schema, params string[] instances) =>
        AssertNeverStricter(name, schema, Profiles, instances);

    private static void CompletesWithin(TimeSpan limit, Action action)
    {
        Task task = Task.Run(action);
        Assert.True(task.Wait(limit), $"did not finish in {limit.TotalSeconds} seconds");
        task.GetAwaiter().GetResult();
    }

    private static string ChainDocument(int links, string root)
    {
        StringBuilder text = new("{");
        text.Append(root).Append(",\"$defs\":{");

        for (int index = 0; index < links; index++)
        {
            text.Append($"\"a{index}\":{{\"properties\":{{\"x\":{{\"$ref\":\"#/$defs/a{index + 1}\"}}}}}},");
        }

        text.Append($"\"a{links}\":{{\"type\":\"string\"}}}}}}");
        return text.ToString();
    }

    private static JsonValueSchema NestedAnyOf(int depth)
    {
        ISchema schema = Z.String();

        for (int index = 0; index < depth; index++)
        {
            schema = Z.AnyOf(schema);
        }

        return (JsonValueSchema)schema;
    }

    private static JsonValueSchema Doubled(int depth)
    {
        ISchema schema = Z.String();

        for (int index = 0; index < depth; index++)
        {
            schema = Z.AnyOf(schema, schema);
        }

        return (JsonValueSchema)schema;
    }

    // 1. Emission must not overflow the stack.

    [Theory]
    [InlineData("\"not\":{\"$ref\":\"#/$defs/a0\"}", "not")]
    [InlineData("\"oneOf\":[{\"$ref\":\"#/$defs/a0\"},{\"type\":\"number\"}]", "oneOf")]
    [InlineData("\"if\":{\"$ref\":\"#/$defs/a0\"},\"then\":{\"type\":\"object\"}", "if")]
    public void A_long_chain_of_references_is_judged_without_using_the_stack(string root, string keyword)
    {
        JsonValueSchema schema = Import(ChainDocument(3_500, root));

        _ = schema.SafeParse("\"x\"");
        Assert.Contains($"\"{keyword}\"", schema.ToJsonSchema().ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_chain_of_references_within_the_limit_is_emitted()
    {
        JsonValueSchema schema = Import(ChainDocument(100, "\"not\":{\"$ref\":\"#/$defs/a0\"}"));

        Assert.Contains("a100", schema.ToJsonSchema().ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_deeply_nested_fluent_schema_is_refused_with_an_exception_not_a_crash()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => NestedAnyOf(1_000).Emit());

        Assert.Contains("256", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(NestedAnyOf(200).Emit());
    }

    [Fact]
    public void A_long_chain_of_lazy_schemas_is_refused_with_an_exception_not_a_crash()
    {
        JsonValueSchema schema = Z.Any();

        for (int index = 0; index < 100_000; index++)
        {
            JsonValueSchema previous = schema;
            schema = Z.Lazy("n" + index, () => previous);
        }

        Assert.Throws<InvalidOperationException>(() => schema.Emit());
    }

    // 2. A rewriter must not make emission exponential.

    [Theory]
    [InlineData(24)]
    [InlineData(30)]
    public void Rewriters_do_not_make_a_schema_that_reuses_itself_take_exponential_time(int depth)
    {
        JsonSchemaOptions options = new() { Target = JsonSchemaProfile.Draft202012.With(Rewriters.ConvertBooleanSchemas) };

        CompletesWithin(TimeSpan.FromSeconds(30), () => Assert.Throws<InvalidOperationException>(() => Doubled(depth).Emit(options)));
    }

    // 3. OpenAPI 3.0 has no null type.

    [Fact]
    public void Null_in_OpenAPI_30_accepts_null_and_nothing_else()
    {
        JsonSchemaOptions options = new() { Target = JsonSchemaProfile.OpenApi30 };
        string text = Z.Null().Emit(options).Schema.ToJsonString();

        Assert.True(Accepts(text, JsonSchemaProfile.OpenApi30, "null"), text);
        Assert.False(Accepts(text, JsonSchemaProfile.OpenApi30, "\"a\""), text);
        Assert.False(Accepts(text, JsonSchemaProfile.OpenApi30, "1"), text);
    }

    [Fact]
    public void Null_beside_other_schemas_in_OpenAPI_30_stays_exact()
    {
        AssertNeverStricter("oneOf null|string", Z.OneOf(Z.Null(), Z.String()), "\"a\"", "null", "1");
        AssertNeverStricter("not null", Z.Not(Z.Null()), "\"a\"", "1", "null");
        AssertNeverStricter("if null", Z.If(Z.Null(), Z.Null(), Z.String()), "\"a\"", "null", "1");
    }

    // 8. An imported format is asserted by Zorg, and a validator may only annotate it.

    [Theory]
    [InlineData("""{"not":{"format":"email"}}""")]
    [InlineData("""{"oneOf":[{"format":"email"},{"format":"ipv4"}]}""")]
    [InlineData("""{"if":{"format":"email"},"then":{"type":"number"}}""")]
    public void An_imported_format_is_not_written_where_a_looser_one_would_make_the_document_stricter(string document) =>
        AssertNeverStricter(document, Import(document), "\"abc\"", "\"a@b.co\"", "\"1.1.1.1\"", "1", "null");

    // 9. OpenAPI 3.0 spellings.

    [Theory]
    [InlineData("""{"not":{"minimum":5,"exclusiveMinimum":1}}""", new[] { "3", "7", "0" })]
    [InlineData("""{"not":{"maximum":1,"exclusiveMaximum":5}}""", new[] { "0", "3", "7" })]
    [InlineData("""{"not":{"const":"a","enum":["a","b"]}}""", new[] { "\"a\"", "\"b\"", "\"c\"" })]
    [InlineData("""{"not":{"type":["string","number"],"anyOf":[{"type":"string","minLength":2},{"type":"number","minimum":3}]}}""", new[] { "\"a\"", "\"abc\"", "1", "4", "true" })]
    [InlineData("""{"type":["string","number"],"anyOf":[{"type":"string","minLength":2},{"type":"number","minimum":3}]}""", new[] { "\"a\"", "\"abc\"", "1", "4", "true" })]
    [InlineData("""{"$schema":"https://json-schema.org/draft/2020-12/schema","type":["string","null"],"nullable":false}""", new[] { "null", "\"a\"" })]
    public void OpenAPI_30_spellings_are_never_stricter_than_parsing(string document, string[] instances) =>
        AssertNeverStricter(document, Import(document), instances);

    // 4 and 5. An imported schema is a document of its own, and its references are relative to its root.

    /// <summary>Parsing and the emitted schema give the same verdict on every instance.</summary>
    private static void AssertAgrees<T>(string name, Schema<T> schema, params string[] instances) =>
        AssertAgrees(name, schema, Profiles, instances);

    private static void AssertAgrees<T>(string name, Schema<T> schema, IEnumerable<JsonSchemaProfile> profiles, params string[] instances)
    {
        foreach (JsonSchemaProfile profile in profiles)
        {
            string text = schema.Emit(new JsonSchemaOptions { Target = profile }).Schema.ToJsonString();

            foreach (string instance in instances)
            {
                Assert.True(
                    schema.SafeParse(instance).IsSuccess == Accepts(text, profile, instance),
                    $"{name} in {profile.Name} disagrees with parsing on {instance}: {text}");
            }
        }
    }

    [Fact]
    public void An_imported_schema_with_definitions_works_inside_a_fluent_one() =>
        AssertAgrees(
            "defs",
            Z.Array(Import("""{"$defs":{"a":{"type":"string"}},"$ref":"#/$defs/a"}""")),
            "[\"x\"]",
            "[1]",
            "[]");

    [Fact]
    public void A_root_reference_in_an_imported_schema_means_the_imported_root() =>
        AssertAgrees(
            "root ref",
            Z.Array(Import("""{"type":"object","properties":{"n":{"$ref":"#"}}}""")),
            "[{\"n\":{\"n\":{}}}]",
            "[{\"n\":{\"n\":1}}]",
            "[{\"n\":1}]");

    [Fact]
    public void A_pointer_in_an_imported_schema_is_relative_to_where_the_schema_sits() =>
        AssertAgrees(
            "pointer",
            Z.Object().Property("p", Import("""{"properties":{"a":{"type":"string"},"b":{"$ref":"#/properties/a"}}}""")),
            """{"p":{"b":"x"}}""",
            """{"p":{"b":1}}""");

    [Fact]
    public void Two_imported_schemas_with_the_same_definition_name_stay_apart() =>
        AssertAgrees(
            "same name",
            Z.Object()
                .Property("x", Import("""{"$defs":{"a":{"type":"string"}},"$ref":"#/$defs/a"}"""))
                .Property("y", Import("""{"$defs":{"a":{"type":"number"}},"$ref":"#/$defs/a"}""")),
            """{"x":"s","y":1}""",
            """{"x":1,"y":1}""",
            """{"x":"s","y":"s"}""");

    [Fact]
    public void An_imported_schema_used_twice_works_in_both_places() =>
        AssertAgrees(
            "twice",
            Z.Object()
                .Property("x", Import("""{"$defs":{"a":{"type":"string"}},"properties":{"v":{"$ref":"#/$defs/a"}}}"""))
                .Property("y", Z.Array(Import("""{"$defs":{"a":{"type":"string"}},"properties":{"v":{"$ref":"#/$defs/a"}}}"""))),
            """{"x":{"v":"s"},"y":[{"v":"s"}]}""",
            """{"x":{"v":1},"y":[]}""",
            """{"x":{},"y":[{"v":1}]}""");

    [Fact]
    public void An_imported_legacy_schema_with_definitions_works_inside_a_fluent_one() =>
        AssertAgrees(
            "draft-07",
            Z.Array(Import("""{"$schema":"http://json-schema.org/draft-07/schema#","definitions":{"a":{"type":"string"}},"$ref":"#/definitions/a"}""")),
            "[\"x\"]",
            "[1]");

    [Fact]
    public void A_reference_prefix_moves_the_definitions_of_an_imported_schema_out()
    {
        JsonValueSchema schema = Import("""{"$defs":{"a":{"type":"string"}},"type":"array","items":{"$ref":"#/$defs/a"}}""");
        EmitResult result = schema.Emit(new JsonSchemaOptions { ReferencePrefix = "#/components/schemas/" });
        string text = result.Schema.ToJsonString();

        Assert.Equal("a", Assert.Single(result.Definitions).Key);
        Assert.DoesNotContain("$defs", text, StringComparison.Ordinal);
        Assert.Contains("\"$ref\":\"#/components/schemas/a\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reference_prefix_moves_the_definitions_of_a_nested_imported_schema_out()
    {
        ObjectSchema schema = Z.Object().Property("list", Import("""{"$defs":{"a":{"type":"string"}},"type":"array","items":{"$ref":"#/$defs/a"}}"""));
        EmitResult result = schema.Emit(new JsonSchemaOptions { ReferencePrefix = "#/components/schemas/" });

        Assert.Equal("a", Assert.Single(result.Definitions).Key);
        Assert.DoesNotContain("$defs", result.Schema.ToJsonString(), StringComparison.Ordinal);
    }

    // 9 (continued). A draft-07 document that carries `nullable`.

    [Fact]
    public void A_nullable_extension_of_a_draft_07_document_is_not_applied_by_OpenAPI_30() =>
        AssertNeverStricter(
            "nullable under not",
            Import("""{"$schema":"http://json-schema.org/draft-07/schema#","not":{"type":"string","nullable":true}}"""),
            "null",
            "1",
            "\"a\"");

    // 10. Draft-07 spellings.

    private const string Draft07Schema = "{\"$schema\":\"http://json-schema.org/draft-07/schema#\",";

    [Fact]
    public void A_draft_07_id_with_a_fragment_keeps_its_base() =>
        AssertAgrees(
            "id with fragment",
            Import(Draft07Schema + """ "definitions":{"a":{"$id":"http://x/y#foo","definitions":{"z":{"type":"string"}},"properties":{"p":{"$ref":"#/definitions/z"}}}},"properties":{"q":{"$ref":"#/definitions/a"}}}"""),
            """{"q":{"p":"s"}}""",
            """{"q":{"p":1}}""");

    [Fact]
    public void A_2020_12_resource_with_an_id_and_an_anchor_keeps_its_base_in_draft_07() =>
        AssertAgrees(
            "id and anchor",
            Import("""{"$id":"http://e.com/r","$defs":{"n":{"$id":"n","$anchor":"a","$defs":{"z":{"type":"string"}},"properties":{"p":{"$ref":"#/$defs/z"}}}},"properties":{"x":{"$ref":"#/$defs/n"}}}"""),
            """{"x":{"p":"s"}}""",
            """{"x":{"p":1}}""");

    [Fact]
    public void A_definitions_extension_does_not_replace_the_defs_of_a_2020_12_document_in_draft_07()
    {
        JsonValueSchema schema = Import("""{"$defs":{"a":{"type":"string"}},"definitions":{"b":{}},"properties":{"x":{"$ref":"#/$defs/a"}}}""");

        AssertAgrees("defs and definitions", schema, """{"x":"s"}""", """{"x":1}""");
        Assert.Contains("\"a\"", schema.Emit(new JsonSchemaOptions { Target = JsonSchemaProfile.Draft07 }).Schema["definitions"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Definitions_that_nothing_refers_to_are_inert_in_draft_07()
    {
        JsonValueSchema schema = Import("""{"definitions":{"d0":{"minLength":-1}},"type":"string"}""");
        JsonObject emitted = schema.Emit(new JsonSchemaOptions { Target = JsonSchemaProfile.Draft07 }).Schema;

        Assert.True(MetaSchemas.Draft7.Evaluate(JsonElement.Parse(emitted.ToJsonString())).IsValid, emitted.ToJsonString());
        Assert.True(emitted.ContainsKey("x-inert-definitions"));
        Assert.False(emitted.ContainsKey("definitions"));
    }

    [Fact]
    public void A_percent_encoded_pointer_is_translated_for_draft_07() =>
        AssertAgrees("pct", Import("""{"$defs":{"a":{"type":"string"}},"$ref":"#/%24defs/a"}"""), "\"a\"", "1");

    [Fact]
    public void A_dependent_required_and_a_dependent_schema_of_one_name_both_apply_in_draft_07()
    {
        const string Document = """{"not":{"dependentRequired":{"a":["b"]},"dependentSchemas":{"a":{"maxProperties":5}}}}""";

        // OpenAPI 3.0 has no dependencies at all, so it leaves the not out.
        AssertAgrees("both dependencies", Import(Document), Profiles.Where(profile => profile != JsonSchemaProfile.OpenApi30), """{"a":1}""", """{"a":1,"b":2}""", """{"b":2}""", """{"a":1,"b":2,"c":3,"d":4,"e":5,"f":6}""");
    }

    // 11. A reference into a location the dialect doesn't write.

    [Theory]
    [InlineData("""{"$schema":"http://json-schema.org/draft-04/schema#","not":{"type":"integer"},"properties":{"a":{"$ref":"#/not"}}}""", new[] { """{"a":1}""", """{"a":1.5}""", """{"a":"s"}""" })]
    [InlineData("""{"$schema":"http://json-schema.org/draft-04/schema#","oneOf":[{"type":"integer"},{"type":"object"}],"properties":{"a":{"$ref":"#/oneOf/0"}}}""", new[] { """{"a":1}""", """{"a":"s"}""" })]
    [InlineData("""{"if":{"type":"string"},"then":{"minLength":1},"properties":{"a":{"$ref":"#/if"}}}""", new[] { """{"a":"s"}""", """{"a":1}""" })]
    [InlineData("""{"patternProperties":{"^x":{"type":"string"}},"properties":{"a":{"$ref":"#/patternProperties/^x"}}}""", new[] { """{"a":"s"}""", """{"a":1}""" })]
    [InlineData("""{"prefixItems":[{"type":"string"}],"items":{"type":"number"},"properties":{"a":{"$ref":"#/items"}}}""", new[] { """{"a":1}""", """{"a":"s"}""" })]
    [InlineData("""{"$schema":"http://json-schema.org/draft-07/schema#","items":[{"type":"string"}],"additionalItems":{"type":"number"},"properties":{"y":{"$ref":"#/additionalItems"}}}""", new[] { """{"y":1}""", """["a",1]""" })]
    [InlineData("""{"definitions":{"a":{"const":"\ud800"}},"$ref":"#/definitions/a"}""", new[] { "\"\\ud800\"" })]
    [InlineData("""{"definitions":{"a":{"anyOf":[{"const":"\ud800"},{"type":"number"}]}},"$ref":"#/definitions/a"}""", new[] { "1", "\"\\ud800\"" })]
    [InlineData("""{"definitions":{"t":{"prefixItems":[{"type":"string"}],"items":{"type":"number"}}},"$ref":"#/definitions/t"}""", new[] { "[\"a\",1]", "[\"a\",\"b\"]" })]
    public void A_reference_into_a_location_that_is_not_written_still_resolves(string document, string[] instances) =>
        AssertNeverStricter(document, Import(document), instances);

    // 12 and 13. Definition names.

    [Fact]
    public void A_root_lazy_schema_with_several_extensions_is_emitted()
    {
        JsonValueSchema schema = Z.Lazy("a", () => Z.String())
            .Meta("x-1", JsonElement.Parse("1"))
            .Meta("x-2", JsonElement.Parse("2"))
            .Meta("x-3", JsonElement.Parse("3"));

        foreach (JsonSchemaProfile profile in Profiles)
        {
            Assert.Null(Record.Exception(() => schema.Emit(new JsonSchemaOptions { Target = profile })));
        }

        AssertNeverStricter("root lazy", schema, "\"a\"");
    }

    [Theory]
    [InlineData("a b")]
    [InlineData("a%20b")]
    [InlineData("a#b")]
    [InlineData("List<Foo>")]
    [InlineData("a\"b")]
    [InlineData("a^b")]
    [InlineData("é")]
    public void A_definition_name_is_written_as_a_valid_reference(string name) =>
        AssertNeverStricter(name, Z.Array(Z.Lazy(name, () => Z.String())), "[\"x\"]", "[]");

    // 15. A rewriter that loosens a subschema must not do it where that makes the document stricter.

    [Theory]
    [InlineData("""{"not":{"minLength":3}}""", new[] { "\"a\"", "\"abc\"", "1" })]
    [InlineData("""{"oneOf":[{"minLength":2},{"maxLength":3}]}""", new[] { "\"a\"", "\"abcd\"", "\"abc\"" })]
    [InlineData("""{"if":{"minLength":3},"then":{"pattern":"^x"}}""", new[] { "\"ab\"", "\"xyz\"", "\"abc\"" })]
    [InlineData("""{"type":"array","contains":{"minLength":3},"maxContains":1}""", new[] { "[\"ab\",\"abc\"]", "[\"abc\"]", "[\"abc\",\"abcd\"]" })]
    [InlineData("""{"minLength":3}""", new[] { "\"abc\"", "\"abcd\"" })]
    public void Relocating_a_keyword_never_makes_the_schema_stricter(string document, string[] instances)
    {
        JsonSchemaProfile profile = JsonSchemaProfile.Draft202012.With(Rewriters.RelocateToDescription("minLength"));

        AssertNeverStricter(document, Import(document), [profile], instances);
    }

    [Fact]
    public void A_keyword_under_not_is_stated_in_the_description_of_the_schema_around_it()
    {
        JsonSchemaProfile profile = JsonSchemaProfile.Draft202012.With(Rewriters.RelocateToDescription("minLength"));
        EmitResult result = Import("""{"not":{"minLength":3}}""").Emit(new JsonSchemaOptions { Target = profile });

        Assert.False(result.Schema.ContainsKey("not"));
        Assert.False(result.Schema.ContainsKey("minLength"));
        Assert.Contains("not", result.Schema["description"]!.ToString(), StringComparison.Ordinal);
        Assert.NotEmpty(result.Diagnostics);
    }

    // 18. Exactness is judged once per subschema.

    [Fact]
    public void A_schema_used_under_many_nots_is_judged_once()
    {
        // Draft-04 integers are checked by a function, which the input schema can't state, so the schema is not exact.
        string properties = string.Join(',', Enumerable.Range(0, 20_000).Select(index => $"\"p{index}\":{{}}"));
        JsonValueSchema shared = Import("{\"$schema\":\"http://json-schema.org/draft-04/schema#\",\"properties\":{" + properties + ",\"zzz\":{\"type\":\"integer\"}}}", new JsonSchemaImportOptions { MaxNodes = 100_000 });
        ISchema[] branches = [.. Enumerable.Range(0, 8_000).Select(_ => (ISchema)Z.Not(shared))];
        JsonSchemaOptions options = new() { Unrepresentable = UnrepresentablePolicy.Omit };

        CompletesWithin(TimeSpan.FromSeconds(15), () => Z.AllOf(branches).Emit(options));
    }
    // Second review.

    [Theory]
    [InlineData("""{"$id":"http://e.com/a","$defs":{"s":{"type":"string"}},"properties":{"p":{"$ref":"#/$defs/s"}}}""", new[] { """{"x":{"p":"v"}}""", """{"x":{"p":1}}""" })]
    [InlineData("""{"$id":"http://e.com/a","properties":{"p":{"type":"string"},"q":{"$ref":"#/properties/p"}}}""", new[] { """{"x":{"q":"v"}}""", """{"x":{"q":1}}""" })]
    [InlineData("""{"$id":"http://e.com/a","type":"object","properties":{"n":{"$ref":"#"}}}""", new[] { """{"x":{"n":{"n":{}}}}""", """{"x":{"n":1}}""" })]
    public void An_imported_schema_with_an_id_of_its_own_keeps_its_references(string document, string[] instances) =>
        AssertAgrees(document, Z.Object().Property("x", Import(document)), instances);

    [Fact]
    public void A_name_made_for_a_moved_target_does_not_replace_a_definition_of_the_document()
    {
        const string Document = """{"$defs":{"target-1":{"type":"string"}},"properties":{"a":{"$ref":"#/$defs/target-1"},"b":{"$ref":"#/if"}},"if":{"type":"number"}}""";

        AssertNeverStricter(Document, Import(Document), """{"a":"x"}""", """{"b":1}""", """{"a":"x","b":2}""");
        AssertAgrees(Document, Import(Document), Profiles.Where(profile => profile == JsonSchemaProfile.OpenApi30), """{"a":"x"}""", """{"a":1}""");
    }

    [Fact]
    public void Redirecting_a_reference_leaves_the_data_of_the_schema_alone()
    {
        const string Document = """{"properties":{"k":{"const":{"$ref":"#/if"}},"e":{"enum":[{"$ref":"#/if"}]},"d":{"default":{"$ref":"#/if"}},"b":{"$ref":"#/if"}},"if":{"type":"number"}}""";

        AssertNeverStricter(Document, Import(Document), """{"k":{"$ref":"#/if"}}""", """{"e":{"$ref":"#/if"}}""", """{"d":{"$ref":"#/if"}}""", """{"b":1}""");
    }

    [Fact]
    public void A_property_named_like_data_still_has_its_references_redirected()
    {
        const string Document = """{"properties":{"const":{"$ref":"#/if"},"enum":{"$ref":"#/if"}},"if":{"type":"number"}}""";

        AssertNeverStricter(Document, Import(Document), """{"const":1}""", """{"enum":2}""", """{}""");
    }

    [Fact]
    public void A_dependent_required_and_a_dependent_schema_do_not_make_a_reference_to_the_schema_stricter()
    {
        const string Document = """{"dependentRequired":{"a":["b"]},"dependentSchemas":{"a":{"type":"object"}},"properties":{"c":{"$ref":"#/dependentSchemas/a"}}}""";

        AssertNeverStricter(Document, Import(Document), """{"c":{}}""", """{"c":{"a":1}}""", """{"a":1,"b":2,"c":{}}""");
    }

    [Fact]
    public void A_definitions_keyword_and_a_defs_keyword_with_one_name_stay_apart_in_draft_07()
    {
        const string Document = """{"$defs":{"x":{"type":"number"}},"definitions":{"x":{"type":"string"}},"properties":{"p":{"$ref":"#/definitions/x"},"q":{"$ref":"#/$defs/x"}}}""";

        AssertNeverStricter(Document, Import(Document), """{"p":"s","q":1}""", """{"p":"s"}""", """{"q":1}""");
    }

    [Theory]
    [InlineData("""{"$defs":{"s":{"maxLength":3}},"properties":{"a":{"not":{"$ref":"#/$defs/s"}}}}""", new[] { """{"a":"hello"}""", """{"a":"he"}""", """{"a":1}""" })]
    [InlineData("""{"$defs":{"s":{"maxLength":3}},"properties":{"a":{"if":{"$ref":"#/$defs/s"},"then":{"type":"string"},"else":{"minLength":5}}}}""", new[] { """{"a":"hello"}""", """{"a":"he"}""", """{"a":"hellos"}""" })]
    [InlineData("""{"$defs":{"s":{"maxLength":3}},"properties":{"a":{"oneOf":[{"$ref":"#/$defs/s"},{"minLength":2}]}}}""", new[] { """{"a":"hello"}""", """{"a":"h"}""", """{"a":"he"}""" })]
    [InlineData("""{"$defs":{"s":{"maxLength":3}},"properties":{"a":{"contains":{"$ref":"#/$defs/s"},"maxContains":1}}}""", new[] { """{"a":["hello"]}""", """{"a":["he","hello"]}""", """{"a":["he","hi"]}""" })]
    [InlineData("""{"$defs":{"s":{"allOf":[{"maxLength":3}]}},"properties":{"a":{"not":{"$ref":"#/$defs/s"}}}}""", new[] { """{"a":"hello"}""" })]
    [InlineData("""{"$defs":{"s":{"maxLength":3}},"properties":{"a":{"$ref":"#/$defs/s"},"b":{"not":{"$ref":"#/$defs/s"}}}}""", new[] { """{"a":"he","b":"hello"}""", """{"a":"hi"}""" })]
    public void Relocating_a_keyword_does_not_loosen_what_a_reference_is_negated_through(string document, string[] instances)
    {
        JsonSchemaProfile profile = JsonSchemaProfile.Draft202012.With(Rewriters.RelocateToDescription("maxLength"));

        AssertNeverStricter(document, Import(document), [profile], instances);
    }

    [Fact]
    public void Relocating_a_keyword_does_not_loosen_what_a_lazy_schema_is_negated_through()
    {
        JsonSchemaProfile profile = JsonSchemaProfile.Draft202012.With(Rewriters.RelocateToDescription("maxLength"));
        ObjectSchema schema = Z.Object().Property("a", Z.Not(Z.Lazy("short", () => Z.String().MaxLength(3))));

        AssertNeverStricter("lazy", schema, [profile], """{"a":"hello"}""", """{"a":1}""");
    }

    [Fact]
    public void A_reference_inside_an_id_resource_to_a_keyword_the_dialect_drops_is_still_resolved()
    {
        const string Document = """{"$id":"http://e.com/a","properties":{"a":{"$ref":"#/if"}},"if":{"type":"number"}}""";

        AssertNeverStricter(Document, Z.Object().Property("x", Import(Document)), """{"x":{"a":1}}""", """{"x":{}}""");
    }

    [Fact]
    public void A_reference_inside_an_id_resource_agrees_with_parsing_where_the_dialect_drops_the_target()
    {
        const string Document = """{"$id":"http://e.com/a","properties":{"a":{"$ref":"#/if"}},"if":{"type":"number"}}""";

        AssertAgrees(Document, Z.Object().Property("x", Import(Document)), """{"x":{"a":1}}""", """{"x":{"a":"s"}}""", """{"x":{}}""");
        AssertAgrees(Document, Import(Document), """{"a":1}""", """{"a":"s"}""");
    }

    [Fact]
    public void An_id_resource_inside_an_imported_schema_is_not_rebased_with_it() =>
        AssertAgrees(
            "resource in import",
            Z.Array(Import("""{"$defs":{"a":{"type":"number"}},"items":{"$id":"http://e.com/n","$defs":{"a":{"type":"string"}},"properties":{"p":{"$ref":"#/$defs/a"}}}}""")),
            """[{"p":"s"}]""",
            """[{"p":1}]""");

    [Fact]
    public void A_root_with_an_id_keeps_its_definitions_beside_a_reference_prefix()
    {
        JsonValueSchema schema = Import("""{"$id":"http://e.com/a","$defs":{"a":{"type":"string"}},"type":"array","items":{"$ref":"#/$defs/a"}}""");
        EmitResult result = schema.Emit(new JsonSchemaOptions { ReferencePrefix = "#/components/schemas/" });
        string text = result.Schema.ToJsonString();

        Assert.Empty(result.Definitions);
        Assert.Contains("\"$ref\":\"#/$defs/a\"", text, StringComparison.Ordinal);
        Assert.True(Accepts(text, JsonSchemaProfile.Draft202012, "[\"x\"]"), text);
        Assert.False(Accepts(text, JsonSchemaProfile.Draft202012, "[1]"), text);
    }

    [Fact]
    public void A_moved_target_in_one_resource_does_not_take_the_name_of_one_in_another()
    {
        const string Document = """{"properties":{"a":{"$ref":"#/if"},"r":{"$id":"http://e.com/r","properties":{"b":{"$ref":"#/if"}},"if":{"type":"string"}}},"if":{"type":"number"}}""";

        AssertAgrees(Document, Import(Document), """{"a":1,"r":{"b":"s"}}""", """{"a":"s"}""", """{"r":{"b":1}}""");
    }

    // Last round: the walks that look for a keyword to move, or for an inexact subschema, are bounded and remember what they found.

    private static JsonValueSchema FreshLazy() => Z.Lazy("n", () => Z.Object().Property("next", FreshLazy()));

    [Theory]
    [InlineData(SchemaDirection.Input)]
    [InlineData(SchemaDirection.Output)]
    public void A_lazy_that_builds_a_new_schema_each_time_is_refused_instead_of_walked_forever(SchemaDirection direction)
    {
        JsonSchemaOptions options = new()
        {
            Direction = direction,
            Target = JsonSchemaProfile.Draft202012.With(Rewriters.RelocateToDescription("minLength")),
        };

        Assert.Throws<InvalidOperationException>(() => Z.Not(FreshLazy()).ToJsonSchema(options));
    }

    [Fact]
    public void A_keyword_search_remembers_what_it_found_in_a_schema_that_many_others_refer_to()
    {
        const int Count = 3000;
        StringBuilder builder = new("{\"$ref\":\"#/$defs/big\",\"$defs\":{\"big\":{\"properties\":{");
        builder.Append(string.Join(',', Enumerable.Range(0, Count).Select(index => $"\"p{index}\":{{\"$ref\":\"#/$defs/d{index}\"}}")));
        builder.Append("}}");

        for (int index = 0; index < Count; index++)
        {
            builder.Append($",\"d{index}\":{{\"not\":{{\"$ref\":\"#/$defs/big\"}}}}");
        }

        builder.Append("}}");
        JsonSchemaOptions options = new() { Target = JsonSchemaProfile.Draft202012.With(Rewriters.RelocateToDescription("minLength")) };
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.NotNull(Import(builder.ToString()).ToJsonSchema(options));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"took {watch.Elapsed}");
    }

    private const string NullableReference = """
        {"$schema":"https://json-schema.org/draft/2020-12/schema",
         "$defs":{"item":{"type":"object","properties":{"id":{"type":"string","format":"uuid"}},"required":["id"]}},
         "type":"object","properties":{"p":{"oneOf":[{"$ref":"#/$defs/item"},{"type":"null"}]}}}
        """;

    [Fact]
    public void A_nullable_reference_to_a_schema_with_a_format_round_trips()
    {
        JsonValueSchema schema = Import(NullableReference);
        EmitResult result = schema.Emit(new JsonSchemaOptions());

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(NullableReference), result.Schema), result.Schema.ToJsonString());
        Assert.Empty(result.Diagnostics);
        Assert.NotNull(schema.Emit(new JsonSchemaOptions { Unrepresentable = UnrepresentablePolicy.Throw }));
    }

    [Theory]
    [InlineData("""{"oneOf":[{"type":"string","format":"uuid"},{"type":"null"}]}""")]
    [InlineData("""{"oneOf":[{"type":"string","format":"date-time"},{"type":"integer"}]}""")]
    [InlineData("""{"oneOf":[{"type":"array","items":{"type":"string","format":"uuid"}},{"type":"null"}]}""")]
    [InlineData("""{"oneOf":[{"type":["string","null"],"format":"uuid"},{"type":"object"}]}""")]
    [InlineData("""{"$defs":{"a":{"$ref":"#/$defs/b"},"b":{"type":"string","format":"uuid"}},"oneOf":[{"$ref":"#/$defs/a"},{"type":"null"}]}""")]
    public void Branches_of_different_types_keep_one_of(string document)
    {
        foreach (JsonSchemaProfile profile in new[] { JsonSchemaProfile.Draft202012, JsonSchemaProfile.Draft07, JsonSchemaProfile.OpenApi30, JsonSchemaProfile.OpenApi31 })
        {
            JsonObject schema = Import(document).ToJsonSchema(new JsonSchemaOptions { Target = profile });

            Assert.True(schema.ContainsKey("oneOf"), schema.ToJsonString());
        }
    }

    [Theory]
    [InlineData("""{"oneOf":[{"type":"string","format":"uuid"},{"type":"string","format":"email"}]}""", "\"abc@example.com\"")]
    [InlineData("""{"oneOf":[{"type":"string","format":"uuid"},{"type":["string","null"]}]}""", "\"abc\"")]
    [InlineData("""{"oneOf":[{"type":"integer","maximum":5,"format":"date"},{"type":"number","minimum":6,"format":"date"}]}""", "1")]
    [InlineData("""{"oneOf":[{"type":"string","format":"uuid"},{"format":"email"}]}""", "\"abc@example.com\"")]
    public void Branches_that_can_overlap_still_relax_to_any_of(string document, string instance)
    {
        JsonValueSchema schema = Import(document);
        string text = schema.ToJsonSchema().ToJsonString();

        Assert.Contains("\"anyOf\"", text, StringComparison.Ordinal);

        if (schema.SafeParse(instance).IsSuccess)
        {
            Assert.True(Accepts(text, JsonSchemaProfile.Draft202012, instance), text);
        }
    }

    [Fact]
    public void A_coerced_branch_overlaps_a_string_branch()
    {
        JsonObject schema = Z.OneOf(Z.Integer().Coerce(), Z.String().Email()).ToJsonSchema();

        Assert.False(schema.ContainsKey("oneOf"), schema.ToJsonString());
        Assert.True(Accepts(schema.ToJsonString(), JsonSchemaProfile.Draft202012, "\"5\""), schema.ToJsonString());
    }

    [Fact]
    public void A_rewriter_keeps_the_relaxed_form_because_it_can_change_a_type()
    {
        JsonSchemaProfile profile = JsonSchemaProfile.Draft202012.With(Rewriters.ConvertBooleanSchemas);
        JsonObject schema = Import(NullableReference).ToJsonSchema(new JsonSchemaOptions { Target = profile });

        Assert.Contains("\"anyOf\"", schema.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Keeping_one_of_does_not_make_not_exact()
    {
        const string Document = """{"not":{"oneOf":[{"type":"string","format":"uuid"},{"type":"null"}]}}""";
        JsonValueSchema schema = Import(Document);

        Assert.True(schema.SafeParse("\"abc\"").IsSuccess);
        Assert.True(Accepts(schema.ToJsonSchema().ToJsonString(), JsonSchemaProfile.Draft202012, "\"abc\""));
    }

    [Fact]
    public void A_recursive_nullable_reference_ends_the_type_walk()
    {
        const string Document = """{"$defs":{"a":{"oneOf":[{"$ref":"#/$defs/n"},{"type":"null"}]},"n":{"type":"object","properties":{"id":{"format":"uuid"},"next":{"$ref":"#/$defs/a"}}}},"$ref":"#/$defs/a"}""";
        string text = Import(Document).ToJsonSchema().ToJsonString();

        Assert.Contains("\"oneOf\"", text, StringComparison.Ordinal);
        Assert.True(Accepts(text, JsonSchemaProfile.Draft202012, """{"next":{"next":null}}"""), text);
    }

    [Fact]
    public void A_copy_of_a_merged_dependency_does_not_repeat_its_id()
    {
        const string Document = """{"dependentRequired":{"a":["b"]},"dependentSchemas":{"a":{"$id":"http://e.com/n","type":"object"}},"properties":{"p":{"$ref":"#/dependentSchemas/a"}}}""";
        string text = Import(Document).ToJsonSchema(new JsonSchemaOptions { Target = JsonSchemaProfile.Draft07 }).ToJsonString();

        Assert.True(Accepts(text, JsonSchemaProfile.Draft07, """{"p":{}}"""), text);
        Assert.False(Accepts(text, JsonSchemaProfile.Draft07, """{"p":1}"""), text);
    }

    [Fact]
    public void A_copy_of_a_merged_dependency_stays_a_resource_for_its_own_references()
    {
        const string Document = """{"required":["z"],"dependentRequired":{"a":["b"]},"dependentSchemas":{"a":{"$id":"http://e.com/n","properties":{"q":{"$ref":"#"}}}},"properties":{"p":{"$ref":"#/dependentSchemas/a"}}}""";
        const string Instance = """{"z":1,"p":{"q":{}}}""";
        string text = Import(Document).ToJsonSchema(new JsonSchemaOptions { Target = JsonSchemaProfile.Draft07 }).ToJsonString();

        Assert.True(Import(Document).SafeParse(Instance).IsSuccess);
        Assert.True(Accepts(text, JsonSchemaProfile.Draft07, Instance), text);
    }

    [Fact]
    public void An_imported_id_does_not_make_a_resource_in_open_api_30()
    {
        const string Document = """{"$id":"http://e.com/a","$defs":{"s":{"type":"string"}},"properties":{"p":{"$ref":"#/$defs/s"}}}""";
        EmitResult result = Z.Object().Property("x", Import(Document)).Emit(new JsonSchemaOptions { Target = JsonSchemaProfile.OpenApi30 });
        string text = result.Schema.ToJsonString();

        // Every reference points at a definition that is there, so the schema reads back.
        Assert.Contains("\"$ref\":\"#/$defs/", text, StringComparison.Ordinal);
        Assert.NotNull(Import(text, new JsonSchemaImportOptions { DefaultDialect = JsonSchemaImportDialect.OpenApi30 }));
        Assert.True(Accepts(text, JsonSchemaProfile.OpenApi30, """{"x":{"p":"s"}}"""), text);
        Assert.False(Accepts(text, JsonSchemaProfile.OpenApi30, """{"x":{"p":1}}"""), text);
    }

    // What the reference fuzzer found, and the three limits docs/import.md once listed.

    [Fact]
    public void A_reference_into_the_allOf_beside_a_wrapped_reference_finds_its_entry() =>
        AssertAgrees(
            "wrapped",
            Import("""{"$defs":{"s":{}},"properties":{"r":{"$ref":"#/$defs/s","allOf":[{"type":"object"}]},"p":{"$ref":"#/properties/r/allOf/0"}}}"""),
            """{"p":{}}""",
            """{"p":1}""");

    [Fact]
    public void A_root_with_an_id_wrapped_in_an_object_keeps_its_references()
    {
        const string Document = """{"$id":"http://e.com/r","anyOf":[{"properties":{"a":{"type":"string"},"b":{"$ref":"#/anyOf/0/properties/a"}}}]}""";
        JsonSchemaProfile profile = JsonSchemaProfile.Draft202012.With(Rewriters.RequireObjectRoot);
        string text = Import(Document).ToJsonSchema(new JsonSchemaOptions { Target = profile }).ToJsonString();

        Assert.True(Accepts(text, profile, """{"data":{"b":"s"}}"""), text);
        Assert.False(Accepts(text, profile, """{"data":{"b":1}}"""), text);
    }

    [Theory]
    [InlineData("""{"if":{"type":"string"},"properties":{"a":{"$ref":"#/if"}}}""")]
    [InlineData("""{"properties":{"a":{"$ref":"#/properties/b"},"b":{"type":"string"}}}""")]
    public void A_reference_prefix_leaves_a_reference_only_to_what_goes_with_the_definitions(string document)
    {
        EmitResult result = Import(document).Emit(new JsonSchemaOptions { Target = JsonSchemaProfile.OpenApi30, ReferencePrefix = "#/components/schemas/" });
        string reference = result.Schema["properties"]!["a"]!["$ref"]!.GetValue<string>();

        Assert.Equal("""{"type":"string"}""", result.Definitions[reference["#/components/schemas/".Length..]].ToJsonString());
    }

    [Fact]
    public void A_reference_prefix_gives_a_recursive_root_a_definition()
    {
        EmitResult result = Z.Array(Import("""{"type":"object","properties":{"n":{"$ref":"#"}}}""")).Emit(new JsonSchemaOptions { ReferencePrefix = "#/components/schemas/" });

        Assert.Equal("""{"type":"object","properties":{"n":{"$ref":"#/components/schemas/target-1"}}}""", result.Definitions["target-1"].ToJsonString());
    }

    [Fact]
    public void An_anchor_on_a_schema_that_was_left_out_is_written_again()
    {
        const string Document = """{"not":{"format":"date","$anchor":"a"},"properties":{"p":{"$ref":"#a"}}}""";
        string text = Import(Document).ToJsonSchema().ToJsonString();

        Assert.True(Accepts(text, JsonSchemaProfile.Draft202012, """{"p":"2024-01-02"}"""), text);
    }

    [Fact]
    public void An_import_used_twice_repeats_neither_its_id_nor_its_anchor()
    {
        JsonValueSchema identified = Import("""{"$defs":{"r":{"$id":"http://e.com/n","type":"string"}},"properties":{"p":{"$ref":"#/$defs/r"}}}""");
        JsonValueSchema anchored = Import("""{"$defs":{"r":{"$anchor":"a","type":"string"}},"properties":{"p":{"$ref":"#a"}}}""");

        foreach (JsonValueSchema schema in new[] { identified, anchored })
        {
            AssertAgrees(
                "twice",
                Z.Object().Property("x", schema).Property("y", schema),
                Profiles,
                """{"x":{"p":"s"},"y":{"p":"s"}}""",
                """{"x":{"p":"s"},"y":{"p":1}}""");
        }
    }

    [Fact]
    public void A_reference_inside_unknown_text_keeps_its_meaning_when_the_text_is_reached()
    {
        const string Document = """{"x-stash":{"k":{"$ref":"#/allOf/1"}},"allOf":[{},{"maxItems":1}],"properties":{"p":{"$ref":"#/x-stash/k"}}}""";

        AssertAgrees("unknown text", Z.AllOf(Import(Document), Z.Any()), """{"p":[1]}""", """{"p":[1,2]}""");
    }

    [Fact]
    public void Open_api_30_reads_no_id_or_anchor_in_an_imported_schema()
    {
        const string Document = """{"$defs":{"a":{"$anchor":"n","type":"string"},"b":{"$id":"http://e.com/b","$defs":{"s":{"type":"string"}},"properties":{"q":{"$ref":"#/$defs/s"}}}},"properties":{"p":{"$ref":"#n"},"r":{"$ref":"#/$defs/b"}}}""";
        string text = Import(Document).ToJsonSchema(new JsonSchemaOptions { Target = JsonSchemaProfile.OpenApi30 }).ToJsonString();

        Assert.DoesNotContain("$anchor", text, StringComparison.Ordinal);
        Assert.DoesNotContain("$id", text, StringComparison.Ordinal);
        Assert.Contains("\"$ref\":\"#/$defs/a\"", text, StringComparison.Ordinal);
        Assert.Contains("\"$ref\":\"#/$defs/b/$defs/s\"", text, StringComparison.Ordinal);
    }
}
