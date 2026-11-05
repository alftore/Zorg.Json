using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Xunit;

namespace Zorg.Json.ComplianceTests;

/// <summary>
/// Reads what <c>z.toJSONSchema(schema, { target: "draft-04" })</c> writes for this Zod schema:
/// <code>
/// z.object({
///   status: z.enum(["active", "inactive"]),
///   retries: z.number().int().default(3),
///   createdAt: z.iso.datetime(),
///   tags: z.array(z.string()),
///   scores: z.record(z.string(), z.number()),
///   shape: z.discriminatedUnion("kind", [
///     z.object({ kind: z.literal("circle"), radius: z.number() }),
///     z.object({ kind: z.literal("square"), side: z.number() }),
///   ]),
///   nickname: z.string().nullable(),
///   id: z.uuid(),
/// })
/// </code>
/// </summary>
public sealed class ZodDraft04Tests
{
    private const string Document = """
        {
          "$schema": "http://json-schema.org/draft-04/schema#",
          "type": "object",
          "properties": {
            "status": { "type": "string", "enum": ["active", "inactive"] },
            "retries": { "default": 3, "type": "integer", "minimum": -9007199254740991, "maximum": 9007199254740991 },
            "createdAt": {
              "type": "string",
              "format": "date-time",
              "pattern": "^(?:(?:\\d\\d[2468][048]|\\d\\d[13579][26]|\\d\\d0[48]|[02468][048]00|[13579][26]00)-02-29|\\d{4}-(?:(?:0[13578]|1[02])-(?:0[1-9]|[12]\\d|3[01])|(?:0[469]|11)-(?:0[1-9]|[12]\\d|30)|(?:02)-(?:0[1-9]|1\\d|2[0-8])))T(?:(?:[01]\\d|2[0-3]):[0-5]\\d(?::[0-5]\\d(?:\\.\\d+)?)?(?:Z))$"
            },
            "tags": { "type": "array", "items": { "type": "string" } },
            "scores": { "type": "object", "additionalProperties": { "type": "number" } },
            "shape": {
              "oneOf": [
                {
                  "type": "object",
                  "properties": { "kind": { "type": "string", "enum": ["circle"] }, "radius": { "type": "number" } },
                  "required": ["kind", "radius"],
                  "additionalProperties": false
                },
                {
                  "type": "object",
                  "properties": { "kind": { "type": "string", "enum": ["square"] }, "side": { "type": "number" } },
                  "required": ["kind", "side"],
                  "additionalProperties": false
                }
              ]
            },
            "nickname": { "anyOf": [{ "type": "string" }, { "type": "null" }] },
            "id": {
              "type": "string",
              "format": "uuid",
              "pattern": "^([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-8][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}|00000000-0000-0000-0000-000000000000|ffffffff-ffff-ffff-ffff-ffffffffffff)$"
            }
          },
          "required": ["status", "retries", "createdAt", "tags", "scores", "shape", "nickname", "id"],
          "additionalProperties": false
        }
        """;

    private const string Valid = """
        {
          "status": "active",
          "retries": 3,
          "createdAt": "2026-11-05T10:00:00Z",
          "tags": ["a"],
          "scores": { "x": 1.5 },
          "shape": { "kind": "circle", "radius": 1 },
          "nickname": null,
          "id": "2eb8aa08-aa98-41ea-b4aa-73b441d16380"
        }
        """;

    private static readonly JsonValueSchema Imported = Z.FromJsonSchema(Document);

    public static TheoryData<string, string> Rejected { get; } = new()
    {
        { "\"active\"", "\"paused\"" },
        { "\"retries\": 3", "\"retries\": 1.5" },
        { "\"retries\": 3", "\"retries\": 9007199254740992" },
        { "2026-11-05T10:00:00Z", "2026-02-30T10:00:00Z" },
        { "[\"a\"]", "[1]" },
        { "\"x\": 1.5", "\"x\": \"high\"" },
        { "\"kind\": \"circle\", \"radius\": 1", "\"kind\": \"circle\", \"side\": 1" },
        { "\"kind\": \"circle\", \"radius\": 1", "\"kind\": \"triangle\"" },
        { "\"nickname\": null", "\"nickname\": 1" },
        { "2eb8aa08-aa98-41ea-b4aa-73b441d16380", "not-a-uuid" },
        { "\"tags\": [\"a\"],", string.Empty },
        { "\"tags\"", "\"labels\"" },
    };

    private static JsonObject Emitted => Imported.ToJsonSchema();

    private static string Change(string from, string to)
    {
        string changed = Valid.Replace(from, to, StringComparison.Ordinal);
        Assert.NotEqual(Valid, changed);
        return changed;
    }

    [Fact]
    public void Accepts_a_matching_value()
    {
        Assert.True(Imported.SafeParse(Valid).IsSuccess);
        Assert.True(Imported.SafeParse(Change("\"nickname\": null", "\"nickname\": \"al\"")).IsSuccess);
        Assert.True(Imported.SafeParse(Change("\"kind\": \"circle\", \"radius\": 1", "\"kind\": \"square\", \"side\": 2")).IsSuccess);
    }

    [Theory]
    [MemberData(nameof(Rejected))]
    public void Rejects_what_the_schema_rules_out(string from, string to) =>
        Assert.False(Imported.SafeParse(Change(from, to)).IsSuccess);

    [Fact]
    public void Default_stays_an_annotation()
    {
        ParseResult<JsonElement> result = Imported.SafeParse(Change("\"retries\": 3,", string.Empty));

        Assert.False(result.IsSuccess);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1e1")]
    public void An_integer_has_no_fraction_or_exponent(string number) =>
        Assert.False(Imported.SafeParse(Change("\"retries\": 3", $"\"retries\": {number}")).IsSuccess);

    [Fact]
    public void Emits_a_valid_document_with_nothing_left_out()
    {
        Assert.True(MetaSchemas.Draft202012.Evaluate(JsonElement.Parse(Emitted.ToJsonString())).IsValid);
        Assert.Empty(Imported.Emit().Diagnostics);
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", Emitted["$schema"]!.GetValue<string>());
    }

    [Theory]
    [MemberData(nameof(Rejected))]
    public void An_independent_validator_agrees_on_the_emitted_schema(string from, string to)
    {
        JsonSchema emitted = JsonSchema.FromText(Emitted.ToJsonString());

        Assert.True(emitted.Evaluate(JsonElement.Parse(Valid)).IsValid);
        Assert.False(emitted.Evaluate(JsonElement.Parse(Change(from, to))).IsValid);
    }

    [Fact]
    public void Emitting_keeps_the_verdicts_except_for_integers_written_as_floats()
    {
        JsonValueSchema again = Z.FromJsonSchema(Emitted.ToJsonString());

        Assert.True(again.SafeParse(Valid).IsSuccess);
        Assert.True(again.SafeParse(Change("\"retries\": 3", "\"retries\": 3.0")).IsSuccess);
    }
}
