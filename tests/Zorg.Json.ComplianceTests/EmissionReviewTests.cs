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
    private static JsonNode? ConvertOpenApi30(JsonNode? node)
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
}
