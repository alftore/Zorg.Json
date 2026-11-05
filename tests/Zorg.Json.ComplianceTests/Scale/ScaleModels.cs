using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zorg.Json.ComplianceTests.Scale;

public sealed record Category(string Name, List<Category> Children);

public sealed record Chain(string Id, Chain? Next);

[JsonSerializable(typeof(Category))]
[JsonSerializable(typeof(Chain))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
public sealed partial class ScaleJson : JsonSerializerContext;
