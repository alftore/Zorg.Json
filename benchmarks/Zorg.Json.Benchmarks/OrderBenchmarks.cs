using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using Json.Schema;

namespace Zorg.Json.Benchmarks;

public sealed record OrderLine(string Sku, int Quantity, decimal Price);

public sealed record Order(string Id, string Email, IReadOnlyList<OrderLine> Lines, string? Note = null);

[JsonSerializable(typeof(Order))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
public sealed partial class BenchmarkJson : JsonSerializerContext;

/// <summary>Validates a typical order document, against System.Text.Json alone and against JsonSchema.Net.</summary>
[MemoryDiagnoser]
public class OrderBenchmarks
{
    private static readonly ObjectSchema<OrderLine> Line = Schema.Object(BenchmarkJson.Default.OrderLine)
        .Property(line => line.Sku, s => s.MinLength(1).MaxLength(32))
        .Property(line => line.Quantity, s => s.Minimum(1))
        .Property(line => line.Price, s => s.Minimum(0m));

    private static readonly ObjectSchema<Order> Typed = Schema.Object(BenchmarkJson.Default.Order)
        .Property(order => order.Id, s => s.Uuid())
        .Property(order => order.Email, s => s.Email())
        .Property(order => order.Lines, s => s.MinItems(1).Items(Line));

    private static readonly ObjectSchema Untyped = Z.Object()
        .Property("id", Z.String().Uuid())
        .Property("email", Z.String().Email())
        .Property("lines", Z.Array(Z.Object()
            .Property("sku", Z.String().MinLength(1).MaxLength(32))
            .Property("quantity", Z.Integer().Minimum(1))
            .Property("price", Z.Number<decimal>().Minimum(0m))).MinItems(1))
        .Property("note", Z.String().Nullable().Optional());

    private static readonly JsonSchema Oracle = JsonSchema.FromText(Typed.ToJsonSchema().ToJsonString());

    private byte[] _json = [];
    private JsonElement _element;

    [Params(1, 100)]
    public int LineCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Order order = new(
            "2eb8aa08-aa98-11ea-b4aa-73b441d16380",
            "ann@example.com",
            [.. Enumerable.Range(0, LineCount).Select(index => new OrderLine($"SKU-{index}", index + 1, 9.5m))]);

        _json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(order, BenchmarkJson.Default.Order));
        _element = JsonElement.Parse(_json);
    }

    [Benchmark(Baseline = true, Description = "System.Text.Json deserialize, no validation")]
    public Order? Deserialize() => JsonSerializer.Deserialize(_json, BenchmarkJson.Default.Order);

    [Benchmark(Description = "Zorg typed SafeParse")]
    public ParseResult<Order> ZorgTyped() => Typed.SafeParse(_json);

    [Benchmark(Description = "Zorg untyped SafeParse")]
    public ParseResult<JsonElement> ZorgUntyped() => Untyped.SafeParse(_element);

    [Benchmark(Description = "JsonSchema.Net Evaluate")]
    public bool JsonSchemaNet() => Oracle.Evaluate(_element).IsValid;
}
