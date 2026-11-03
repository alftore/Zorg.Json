using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zorg.Json.Llm.Tests;

internal static class Samples
{
    public static readonly ObjectSchema Category = Z.Object()
        .Property("name", Z.String().MinLength(1))
        .Property("children", Z.Array(Z.Lazy("category", () => Category!)).Optional());

    /// <summary>Nested objects, optional fields, nullable, enum, numeric limits, recursion and a refinement.</summary>
    public static readonly ObjectSchema Order = Z.Object()
        .Description("A customer order")
        .Property("id", Z.String().Uuid())
        .Property("customer", Z.Object()
            .Property("name", Z.String().MinLength(1).MaxLength(100))
            .Property("email", Z.String().Email().Optional())
            .Property("nickname", Z.String().Nullable()))
        .Property("status", Z.String().Enum("open", "paid", "shipped"))
        .Property("quantity", Z.Integer().Minimum(1).Maximum(99))
        .Property("price", Z.Number().ExclusiveMinimum(0).MultipleOf(0.01))
        .Property("priority", Z.Integer().Default(3))
        .Property("note", Z.String().Nullable().Optional())
        .Property("tags", Z.Array(Z.String()).MinItems(1).MaxItems(5))
        .Property("category", Category)
        .Property("code", Z.String().Pattern("^Z[0-9]+$").Refine(code => code.Length % 2 == 0, "The code must have an even length"));

    public const string ValidOrder = """
        {
          "id": "6f1c1f3e-8f3a-4c58-9d6b-2f7a5d3e1b10",
          "customer": { "name": "Ada", "nickname": null },
          "status": "open",
          "quantity": 2,
          "price": 9.99,
          "tags": ["a"],
          "category": { "name": "Books" },
          "code": "Z123"
        }
        """;

    /// <summary>The same order as a model bound by a required-and-nullable schema writes it.</summary>
    public const string ValidOrderFromStrictModel = """
        {
          "id": "6f1c1f3e-8f3a-4c58-9d6b-2f7a5d3e1b10",
          "customer": { "name": "Ada", "email": null, "nickname": null },
          "status": "open",
          "quantity": 2,
          "price": 9.99,
          "priority": null,
          "note": null,
          "tags": ["a"],
          "category": { "name": "Books", "children": null },
          "code": "Z123"
        }
        """;

    private static readonly JsonSerializerOptions Indented = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public static string Json(JsonNode schema) => schema.ToJsonString(Indented);
}
