#pragma warning disable CA1031 // The harness catches everything on purpose: any exception is a finding.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Json.Schema;
using Zorg.Json.Emission;
using Zorg.Json.Import;
using Zorg.Json.Llm;

namespace Zorg.Json.ComplianceTests.Fuzzing;

/// <summary>One document, how it is embedded in a fluent schema, and how a value for it is wrapped to suit.</summary>
internal sealed record ReferenceCase(
    string Scenario,
    string Document,
    string Second,
    JsonSchemaImportDialect Dialect,
    ISchema Schema,
    IProbe Probe,
    Func<JsonNode?, JsonNode?, JsonNode?> Wrap)
{
    public static readonly string[] Scenarios =
    [
        "plain", "plain", "plain", "property", "array", "nested-twice", "used-twice", "two-imports", "any-of", "all-of",
        "named-twice", "two-named", "lazy-import", "lazy-tree", "named-in-named",
    ];

    /// <summary>Null when the document is refused, which is a clean answer.</summary>
    public static ReferenceCase? Build(string scenario, string document, string second, JsonSchemaImportDialect dialect, Action<string>? refused = null)
    {
        JsonValueSchema one;
        JsonValueSchema two;

        // The generated documents are small, so this limit only stops an import that doesn't end. On a thread with a large stack that's a finding, not a dead process.
        JsonSchemaImportOptions options = new() { DefaultDialect = dialect, MaxNodes = 5_000 };

        try
        {
            one = Z.FromJsonSchema(document, options);
            two = Z.FromJsonSchema(second, options);
        }
        catch (JsonSchemaImportException exception)
        {
            refused?.Invoke(exception.Code);
            return null;
        }

        static JsonNode? Copy(JsonNode? node) => node?.DeepClone();

        ReferenceCase Case<T>(Schema<T> schema, Func<JsonNode?, JsonNode?, JsonNode?> wrap) =>
            new(scenario, document, second, dialect, schema, new Probe<T>(schema), wrap);

        switch (scenario)
        {
            case "plain":
                return Case(one, (a, _) => Copy(a));
            case "property":
                return Case(Z.Object().Property("x", one), (a, _) => new JsonObject { ["x"] = Copy(a) });
            case "array":
                return Case(Z.Array(one), (a, _) => new JsonArray(Copy(a)));
            case "nested-twice":
                return Case(
                    Z.Object().Property("x", Z.Array(Z.Object().Property("y", one))),
                    (a, _) => new JsonObject { ["x"] = new JsonArray(new JsonObject { ["y"] = Copy(a) }) });
            case "used-twice":
                return Case(Z.Object().Property("x", one).Property("y", one), (a, b) => new JsonObject { ["x"] = Copy(a), ["y"] = Copy(a) });
            case "two-imports":
                return Case(Z.Object().Property("x", one).Property("y", two), (a, b) => new JsonObject { ["x"] = Copy(a), ["y"] = Copy(b) });
            case "any-of":
                return Case(Z.AnyOf(one, Z.Null()), (a, _) => Copy(a));
            case "all-of":
                return Case(Z.AllOf(one, Z.Any()), (a, _) => Copy(a));
            case "named-twice":
                JsonValueSchema named = one.Named("shared");
                return Case(Z.Object().Property("x", named).Property("y", named), (a, _) => new JsonObject { ["x"] = Copy(a), ["y"] = Copy(a) });
            case "two-named":
                return Case(Z.Object().Property("x", one.Named("A")).Property("y", two.Named("B")), (a, b) => new JsonObject { ["x"] = Copy(a), ["y"] = Copy(b) });
            case "lazy-import":
                return Case(Z.Object().Property("x", Z.Lazy("imported", () => one)), (a, _) => new JsonObject { ["x"] = Copy(a) });
            case "lazy-tree":
                ObjectSchema? tree = null;
                tree = Z.Object().Property("doc", one).Property("next", Z.Lazy("tree", () => tree!).Optional());
                return Case(tree, (a, _) => new JsonObject { ["doc"] = Copy(a), ["next"] = new JsonObject { ["doc"] = Copy(a) } });
            case "named-in-named":
                ObjectSchema outer = Z.Object().Property("y", one.Named("inner")).Named("outer");
                return Case(
                    Z.Object().Property("x", outer).Property("z", outer),
                    (a, _) => new JsonObject { ["x"] = new JsonObject { ["y"] = Copy(a) }, ["z"] = new JsonObject { ["y"] = Copy(a) } });
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }
    }
}

/// <summary>A broken promise, with what it takes to repeat it: the variant, and the value when a value showed it.</summary>
internal sealed record ReferenceFailure(string Kind, string Profile, string Variant, string Detail, string? Instance);

/// <summary>How a schema is emitted. The checks run on every one of these.</summary>
internal sealed record ReferenceVariant(string Profile, string Name, JsonSchemaProfile Target, JsonSchemaOptions Options)
{
    private const string Prefix = "#/components/schemas/";

    private static readonly (JsonSchemaProfile Profile, string Name)[] Profiles =
    [
        (JsonSchemaProfile.Draft202012, "Draft 2020-12"),
        (JsonSchemaProfile.Draft07, "Draft-07"),
        (JsonSchemaProfile.OpenApi30, "OpenAPI 3.0"),
        (JsonSchemaProfile.OpenApi31, "OpenAPI 3.1"),
    ];

    public static readonly ReferenceVariant[] All =
    [
        .. Profiles.SelectMany(entry => Of(entry.Profile, entry.Name)),
        Relocating("OpenAI strict", LlmProfiles.OpenAIStrict),
        Relocating("Anthropic strict", LlmProfiles.AnthropicStrict),
        Relocating("Gemini JSON Schema", LlmProfiles.GeminiJsonSchema),

        // Its types are in upper case, which JsonSchema.Net can't read, so only the references are judged.
        Relocating("Gemini OpenAPI", LlmProfiles.GeminiOpenApi) with { IsLoadable = false },
    ];

    /// <summary>A profile that closes objects or requires properties is meant to be stricter than parsing, so its verdicts on values prove nothing.</summary>
    public bool ChecksValues { get; init; } = true;

    public bool IsLoadable { get; init; } = true;

    public bool IsInput => Options.Direction == SchemaDirection.Input;

    public bool HasPrefix => Options.ReferencePrefix is not null;

    public bool WrapsRoot => Target.Rewriters.Contains(Rewriters.RequireObjectRoot);

    private static ReferenceVariant Relocating(string profile, JsonSchemaProfile target, string name = "base") =>
        new(profile, name, target, new() { Target = target }) { ChecksValues = false };

    private static IEnumerable<ReferenceVariant> Of(JsonSchemaProfile profile, string name)
    {
        JsonSchemaProfile objectRoot = profile.With(Rewriters.RequireObjectRoot);
        yield return Relocating(name, profile.With(Rewriters.CloseAllObjects), "close-all");
        yield return new(name, "base", profile, new() { Target = profile });
        yield return new(name, "output", profile, new() { Target = profile, Direction = SchemaDirection.Output });
        yield return new(name, "reused-ref", profile, new() { Target = profile, Reused = ReusedSchemaHandling.Ref });
        yield return new(name, "prefix", profile, new() { Target = profile, ReferencePrefix = Prefix });
        yield return new(name, "prefix+reused-ref", profile, new() { Target = profile, ReferencePrefix = Prefix, Reused = ReusedSchemaHandling.Ref });
        yield return new(name, "object-root", objectRoot, new() { Target = objectRoot });
        yield return new(name, "object-root+reused-ref", objectRoot, new() { Target = objectRoot, Reused = ReusedSchemaHandling.Ref });
    }
}

/// <summary>
/// The four judges of the reference fuzzer, and a fifth that only reports. (a) No <c>$ref</c> dangles or lands on what
/// isn't a schema. (b) JsonSchema.Net loads the document and can run it. (c) Everything <c>SafeParse</c> accepts, the
/// document accepts. (d) Emit doesn't crash or hang, though <see cref="InvalidOperationException"/> states a limit. (e) With no
/// diagnostics the document claims to be exact, so what parsing rejects it rejects too. That catches a reference aimed at the wrong place.
/// </summary>
internal static partial class ReferenceChecker
{
    /// <summary>The document that a <see cref="JsonSchemaOptions.ReferencePrefix"/> leaves for its caller to write: the root and the definitions, as components of one document.</summary>
    private const string RootComponent = "zorg-root";

    /// <summary>A kind that doesn't count as a failure. JsonSchema.Net gave up on a document whose references all land on schemas by <see cref="ReferenceGraph"/>.</summary>
    public const string OracleLimit = "n-oracle-gave-up-on-sound-references";

    private static readonly TimeSpan EmitTimeout = TimeSpan.FromSeconds(20);

    /// <summary>A generated document is tiny, so an emit that takes this long has run away, even if it would have ended.</summary>
    private static readonly TimeSpan EmitBudget = TimeSpan.FromSeconds(3);

    /// <summary>
    /// JsonSchema.Net sees a cycle in a reference to a child of the same schema, finds no anchor under keywords it builds late,
    /// such as <c>dependentSchemas</c>, and registers an <c>$id</c> or an anchor twice when a reference reaches it before the walk does.
    /// <c>JsonSchema.Net refuses_a_sound_document</c> holds an example.
    /// </summary>
    private static bool IsOracleLimit(Exception exception) =>
        exception is RefResolutionException
        || exception.Message.Contains("Cycle detected", StringComparison.Ordinal)
        || exception.Message.Contains("Overwriting registered schemas", StringComparison.Ordinal)
        || exception.Message.Contains("same key has already been added", StringComparison.Ordinal);

    /// <summary>Every failure of one case under one variant. <paramref name="emit"/> stands in for the emitter, to prove that a judge can fail.</summary>
    public static List<ReferenceFailure> Check(ReferenceCase @case, ReferenceVariant variant, IReadOnlyList<JsonElement> instances, Func<JsonSchemaOptions, EmitResult>? emit = null, ReferenceCounters? counters = null, TimeSpan? timeout = null, TimeSpan? budget = null)
    {
        List<ReferenceFailure> failures = [];

        void Fail(string kind, string detail, string? instance = null) =>
            failures.Add(new ReferenceFailure(kind, variant.Profile, variant.Name, detail, instance));

        Stopwatch clock = Stopwatch.StartNew();
        EmitResult? result = Emit(emit ?? @case.Probe.Emit, variant, Fail, counters, timeout ?? EmitTimeout);

        if (clock.Elapsed > (budget ?? EmitBudget) && failures.Count == 0)
        {
            // A busy machine can pause any one run. Work that really blows up is slow every time.
            clock.Restart();
            _ = Emit(emit ?? @case.Probe.Emit, variant, static (_, _, _) => { }, null, timeout ?? EmitTimeout);

            if (clock.Elapsed > (budget ?? EmitBudget))
            {
                Fail("d-emit-slow", $"{clock.Elapsed.TotalSeconds:F1} seconds for a small document");
            }
        }

        if (result is null)
        {
            return failures;
        }

        (JsonObject document, string? container) = Compose(result, variant);
        JsonSchemaDialect dialect = variant.Target.Dialect;

        List<(string Kind, string Detail)> broken = ReferenceGraph.Check(document, dialect, container);

        foreach ((string kind, string detail) in broken)
        {
            Fail(kind, detail);
        }

        // JsonSchema.Net refuses some sound documents: it sees a cycle in a reference to a child, and finds no anchor under dependentSchemas.
        // With the references sound by the reading above, that's its limit, not a defect here, and the report shows them apart.
        bool isSound = broken.Count == 0;

        if (!variant.IsLoadable)
        {
            return failures;
        }

        JsonObject loadable = Loadable(document, dialect);
        JsonSchema? oracle;

        try
        {
            // A resource inside the document reads no $schema of its own, and without the dialect here it would refuse unknown keywords.
            oracle = JsonSchema.FromText(loadable.ToJsonString(), new BuildOptions
            {
                SchemaRegistry = new SchemaRegistry(),
                Dialect = dialect == JsonSchemaDialect.Draft07 ? Dialect.Draft07 : Dialect.Draft202012,
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Fail(isSound && IsOracleLimit(exception) ? OracleLimit : "b-does-not-load", $"{exception.GetType().Name}: {exception.Message}");
            ReadBack(@case, variant, document, container, instances, Fail, counters, failures);
            return failures;
        }

        if (!variant.IsInput || !variant.ChecksValues)
        {
            return failures;
        }

        bool isExact = result.Diagnostics.IsEmpty && !@case.Document.Contains("\"format\"", StringComparison.Ordinal) && !@case.Second.Contains("\"format\"", StringComparison.Ordinal);
        bool wraps = variant.WrapsRoot && !Rewriters.HasObjectRoot(@case.Schema.ToNode());

        foreach (JsonElement instance in instances)
        {
            Judge(@case, oracle, instance, wraps, isExact, isSound, Fail);
        }

        ReadBack(@case, variant, document, container, instances, Fail, counters, failures);
        return failures;
    }

    /// <summary>
    /// Judge (g), for a document JsonSchema.Net gave up on, which would otherwise get judge (a) alone. Zorg's importer reads the
    /// emitted document, and everything parsing accepted must still be accepted. The importer resolves references by a path of its
    /// own, so a pointer aimed at the wrong schema shows up as a different verdict.
    /// </summary>
    private static void ReadBack(ReferenceCase @case, ReferenceVariant variant, JsonObject document, string? container, IReadOnlyList<JsonElement> instances, Action<string, string, string?> fail, ReferenceCounters? counters, List<ReferenceFailure> failures)
    {
        if (!failures.Any(failure => failure.Kind == OracleLimit) || !variant.IsInput || !variant.ChecksValues || variant.WrapsRoot || container is not null)
        {
            return;
        }

        JsonValueSchema readBack;

        try
        {
            readBack = Z.FromJsonSchema(document.ToJsonString(), new JsonSchemaImportOptions
            {
                DefaultDialect = variant.Target.Dialect switch
                {
                    JsonSchemaDialect.Draft07 => JsonSchemaImportDialect.Draft07,
                    JsonSchemaDialect.OpenApi30 => JsonSchemaImportDialect.OpenApi30,
                    _ => JsonSchemaImportDialect.Draft202012,
                },
            });
        }
        catch (JsonSchemaImportException exception)
        {
            counters?.Count($"read back refused: {exception.Code}");
            return;
        }

        counters?.Count("read back judged");

        foreach (JsonElement instance in instances)
        {
            if (@case.Probe.ParseElement(instance) && !readBack.SafeParse(instance.GetRawText()).IsSuccess)
            {
                fail("g-stricter-when-read-back", "SafeParse accepts the value, and the emitted schema read back rejects it", instance.GetRawText());
            }
        }
    }

    private static void Judge(ReferenceCase @case, JsonSchema oracle, JsonElement instance, bool wraps, bool isExact, bool isSound, Action<string, string, string?> fail)
    {
        bool parsed;

        try
        {
            parsed = @case.Probe.ParseElement(instance);
        }
        catch (Exception exception)
        {
            fail("f-safeparse-threw", $"{exception.GetType().Name}: {exception.Message}", instance.GetRawText());
            return;
        }

        JsonElement written = wraps ? JsonElement.Parse($$"""{"{{Rewriters.RootWrapperProperty}}":{{instance.GetRawText()}}}""") : instance;
        bool accepted;

        try
        {
            accepted = oracle.Evaluate(written, new EvaluationOptions()).IsValid;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            fail(isSound && IsOracleLimit(exception) ? OracleLimit : "b-cannot-evaluate", $"{exception.GetType().Name}: {exception.Message}", instance.GetRawText());
            return;
        }

        if (parsed && !accepted)
        {
            fail("c-stricter-than-parsing", "SafeParse accepts the value and the emitted schema rejects it", instance.GetRawText());
        }
        else if (!parsed && accepted && isExact)
        {
            fail("e-looser-with-no-diagnostic", "SafeParse rejects the value, and the emitted schema accepts it with no diagnostic", instance.GetRawText());
        }
    }

    private static EmitResult? Emit(Func<JsonSchemaOptions, EmitResult> emit, ReferenceVariant variant, Action<string, string, string?> fail, ReferenceCounters? counters, TimeSpan timeout)
    {
        Task<EmitResult> task = Task.Run(() => emit(variant.Options));

        try
        {
            if (!task.Wait(timeout))
            {
                fail("d-emit-hung", $"no answer in {timeout.TotalSeconds} seconds", null);
                return null;
            }

            return task.Result;
        }
        catch (AggregateException wrapped) when (wrapped.InnerException is InvalidOperationException stated)
        {
            counters?.Count($"stated limit: {stated.Message[..Math.Min(60, stated.Message.Length)]}");
            return null;
        }
        catch (AggregateException wrapped)
        {
            Exception exception = wrapped.InnerException ?? wrapped;
            fail("d-emit-crashed", $"{exception.GetType().Name}: {exception.Message}", null);
            return null;
        }
    }

    /// <summary>
    /// The real document for a variant. With a prefix, the root goes wherever the caller puts it, which isn't the root of
    /// the document that holds the definitions, so that's how it's judged: as one more component beside them.
    /// </summary>
    private static (JsonObject Document, string? Container) Compose(EmitResult result, ReferenceVariant variant)
    {
        if (!variant.HasPrefix)
        {
            return ((JsonObject)result.Schema.DeepClone(), null);
        }

        JsonObject root = (JsonObject)result.Schema.DeepClone();
        JsonNode? dialect = root["$schema"]?.DeepClone();
        root.Remove("$schema");
        JsonObject schemas = new() { [RootComponent] = root };

        foreach ((string name, JsonObject definition) in result.Definitions)
        {
            schemas[name] = definition.DeepClone();
        }

        JsonObject document = new() { ["$ref"] = $"#/components/schemas/{RootComponent}", ["components"] = new JsonObject { ["schemas"] = schemas } };

        if (dialect is not null)
        {
            document.Insert(0, "$schema", dialect);
        }

        return (document, "components");
    }

    /// <summary>
    /// Judge (h). Schemas with a prefix share one document, as the endpoints of an OpenAPI document do. A name the emitter made up
    /// must not clash, and every reference must still land once all of them sit together. A clash of names from the documents themselves
    /// is the caller's, and only counted.
    /// </summary>
    public static List<(string Kind, string Detail)> CheckComponents(IReadOnlyList<EmitResult> results, ReferenceCounters? counters = null)
    {
        List<(string Kind, string Detail)> found = [];
        JsonObject schemas = [];

        for (int index = 0; index < results.Count; index++)
        {
            schemas[$"{RootComponent}-{index.ToString(CultureInfo.InvariantCulture)}"] = results[index].Schema.DeepClone();

            foreach ((string name, JsonObject definition) in results[index].Definitions)
            {
                if (schemas[name] is not { } held)
                {
                    schemas[name] = definition.DeepClone();
                }
                else if (!JsonNode.DeepEquals(held, definition))
                {
                    if (!MadeUpName().IsMatch(name))
                    {
                        counters?.Count("components: documents with one name for two definitions");
                        return [];
                    }

                    found.Add(("h-components-clash", $"two definitions are named {name}"));
                }
            }
        }

        if (found.Count == 0)
        {
            foreach ((string kind, string detail) in ReferenceGraph.Check(new JsonObject { ["components"] = new JsonObject { ["schemas"] = schemas } }, JsonSchemaDialect.OpenApi31, "components"))
            {
                // Each root is written whole, so one schema on two endpoints repeats what it declares. No reference is the worse for it.
                if (kind is "a-duplicate-id" or "a-duplicate-anchor")
                {
                    counters?.Count("components: a repeated $id or $anchor across documents");
                }
                else
                {
                    found.Add((kind, detail));
                }
            }
        }

        return found;
    }

    public static bool SameDefinitions(EmitResult left, EmitResult right) =>
        left.Definitions.Count == right.Definitions.Count
        && left.Definitions.All(entry => right.Definitions.TryGetValue(entry.Key, out JsonObject? other) && JsonNode.DeepEquals(entry.Value, other));

    [GeneratedRegex("-[0-9a-f]{8}[0-9]*$")]
    private static partial Regex MadeUpName();

    /// <summary>OpenAPI 3.0 reads neither <c>$id</c> nor <c>$anchor</c>, so its documents are judged without them.</summary>
    private static void RemoveIdentity(JsonNode? node)
    {
        if (node is JsonObject schema)
        {
            schema.Remove("$id");
            schema.Remove("$anchor");

            foreach ((string _, JsonNode? child) in schema)
            {
                RemoveIdentity(child);
            }
        }
        else if (node is JsonArray list)
        {
            foreach (JsonNode? child in list)
            {
                RemoveIdentity(child);
            }
        }
    }

    /// <summary>What JsonSchema.Net can read: OpenAPI 3.0 translated into 2020-12, what's meant to be inert removed, and <c>$schema</c> present.</summary>
    private static JsonObject Loadable(JsonObject document, JsonSchemaDialect dialect)
    {
        JsonObject copy = (JsonObject)document.DeepClone();

        if (dialect == JsonSchemaDialect.OpenApi30)
        {
            if (copy["components"]?["schemas"] is JsonObject schemas)
            {
                foreach (string name in schemas.Select(entry => entry.Key).ToArray())
                {
                    schemas[name] = EmissionReviewTests.ConvertOpenApi30(schemas[name]);
                }
            }
            else
            {
                copy = (JsonObject)EmissionReviewTests.ConvertOpenApi30(copy)!;
            }

            RemoveIdentity(copy);
        }

        Oracle.RemoveInertKeywords(copy);

        // OpenAPI 3.1 output has no $schema, and without one JsonSchema.Net refuses every unknown keyword.
        if (!copy.ContainsKey("$schema"))
        {
            copy.Insert(0, "$schema", "https://json-schema.org/draft/2020-12/schema");
        }

        return copy;
    }
}

internal static class ReferenceBaseline
{
    /// <summary>
    /// What each kind may reach in the fixed run: the documents, out of those judged, that failed that way when the kind was
    /// last accepted. A kind or profile that isn't listed fails the run, and so does a count above what's listed. Lower the numbers as defects are fixed.
    /// </summary>
    public static readonly Dictionary<(string Kind, string Profile), int> Accepted = [];
}

/// <summary>Counts that aren't failures, such as refused documents, shown in the report so that nothing is filtered silently.</summary>
internal sealed class ReferenceCounters
{
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

    public void Count(string name) => _counts.AddOrUpdate(name, 1, (_, count) => count + 1);

    public int this[string name] => _counts.GetValueOrDefault(name);

    public IEnumerable<KeyValuePair<string, int>> All => _counts.OrderBy(entry => entry.Key, StringComparer.Ordinal);
}

/// <summary>Makes values for a document by following its keywords and references, so that the places references reach are visited.</summary>
internal sealed class ReferenceInstances(Random random, JsonObject document)
{
    private static readonly string[] Pool = ["\"a\"", "\"bb\"", "\"2024-01-02\"", "1", "7", "2.5", "true", "null", "[]", "{}", "[1,\"a\"]", "{\"a\":1}"];

    private static JsonNode? Scalar(string text) => JsonNode.Parse(text);

    public IEnumerable<JsonNode?> Make(int count)
    {
        for (int index = 0; index < count; index++)
        {
            yield return Value(document, document, 0);
        }

        yield return Scalar(Pool[random.Next(Pool.Length)]);
    }

    private JsonNode? Value(JsonNode? schema, JsonObject resource, int depth)
    {
        if (schema is not JsonObject node || depth > 7)
        {
            return Scalar(Pool[random.Next(Pool.Length)]);
        }

        if (node["$id"] is JsonValue id && id.TryGetValue(out string? text) && !text.StartsWith('#'))
        {
            resource = node;
        }

        if (node["$ref"] is JsonValue reference && reference.TryGetValue(out string? target) && random.Next(8) != 0)
        {
            return Value(Follow(target, resource), resource, depth + 1);
        }

        foreach (string keyword in (ReadOnlySpan<string>)["anyOf", "oneOf"])
        {
            if (node[keyword] is JsonArray branches && branches.Count > 0)
            {
                return Value(branches[random.Next(branches.Count)], resource, depth + 1);
            }
        }

        if (node["allOf"] is JsonArray all && all.Count > 0 && !node.ContainsKey("properties"))
        {
            return Merge(all, resource, depth);
        }

        if (node["enum"] is JsonArray values && values.Count > 0)
        {
            return values[random.Next(values.Count)]?.DeepClone();
        }

        if (node["const"] is { } constant)
        {
            return constant.DeepClone();
        }

        if (node["properties"] is JsonObject || node["required"] is JsonArray || node["dependentSchemas"] is JsonObject || node["patternProperties"] is JsonObject)
        {
            return Object(node, resource, depth);
        }

        if (node["items"] is not null || node["prefixItems"] is not null || node["contains"] is not null)
        {
            return List(node, resource, depth);
        }

        return (node["type"] as JsonValue)?.GetValue<string>() switch
        {
            "string" => Scalar("\"bb\""),
            "integer" => Scalar("3"),
            "object" => new JsonObject(),
            "array" => new JsonArray(),
            _ => Scalar(Pool[random.Next(Pool.Length)]),
        };
    }

    private JsonNode? Merge(JsonArray branches, JsonObject resource, int depth)
    {
        JsonObject merged = [];

        foreach (JsonNode? branch in branches)
        {
            JsonNode? value = Value(branch, resource, depth + 1);

            if (value is not JsonObject members)
            {
                return value;
            }

            foreach ((string name, JsonNode? member) in members)
            {
                merged[name] = member?.DeepClone();
            }
        }

        return merged;
    }

    private JsonObject Object(JsonObject node, JsonObject resource, int depth)
    {
        JsonObject value = [];

        foreach ((string name, JsonNode? schema) in node["properties"] as JsonObject ?? [])
        {
            if (random.Next(4) != 0)
            {
                value[name] = Value(schema, resource, depth + 1);
            }
        }

        foreach (string name in (node["required"] as JsonArray ?? []).OfType<JsonValue>().Select(item => item.GetValue<string>()))
        {
            value[name] ??= Scalar(Pool[random.Next(Pool.Length)]);
        }

        foreach (string keyword in (ReadOnlySpan<string>)["dependentSchemas", "dependencies", "patternProperties"])
        {
            foreach ((string name, JsonNode? schema) in node[keyword] as JsonObject ?? [])
            {
                if (random.Next(2) == 0 && schema is JsonObject)
                {
                    value[keyword == "patternProperties" ? name.Trim('^', '$', '+') : name] = Value(schema, resource, depth + 1);
                }
            }
        }

        return value;
    }

    private JsonArray List(JsonObject node, JsonObject resource, int depth)
    {
        JsonArray value = [];

        foreach (string keyword in (ReadOnlySpan<string>)["prefixItems", "items"])
        {
            if (node[keyword] is JsonArray tuple)
            {
                foreach (JsonNode? item in tuple)
                {
                    value.Add(Value(item, resource, depth + 1));
                }
            }
        }

        foreach (string keyword in (ReadOnlySpan<string>)["items", "additionalItems", "contains"])
        {
            if (node[keyword] is JsonObject single)
            {
                value.Add(Value(single, resource, depth + 1));
            }
        }

        return value;
    }

    private static JsonNode? Follow(string reference, JsonObject resource)
    {
        string fragment = Uri.UnescapeDataString(reference.TrimStart('#'));
        JsonNode? current = resource;

        if (fragment.Length > 0 && fragment[0] != '/')
        {
            return null;
        }

        foreach (string token in fragment.Split('/').Skip(1))
        {
            string name = token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

            current = current switch
            {
                JsonObject members => members[name],
                JsonArray items when int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index < items.Count => items[index],
                _ => null,
            };
        }

        return current;
    }
}

/// <summary>Takes parts out of a document while it still fails the same way, which leaves its shape.</summary>
internal static class ReferenceShrinker
{
    private const int MaxAttempts = 200;

    /// <summary>The smallest pair of documents that still fails this way, within the attempts allowed.</summary>
    public static (string Document, string Second) Shrink(ReferenceFailure failure, ReferenceCase @case, ReferenceVariant variant, Func<ReferenceCase, JsonSchemaOptions, EmitResult>? emit = null)
    {
        string first = Reduce(@case.Document, text => Fails(failure, @case.Scenario, text, @case.Second, @case.Dialect, variant, emit));
        string second = Reduce(@case.Second, text => Fails(failure, @case.Scenario, first, text, @case.Dialect, variant, emit));
        return (first, second);
    }

    private static string Reduce(string document, Func<string, bool> fails)
    {
        JsonNode current = JsonNode.Parse(document)!;
        int attempts = 0;
        bool changed = true;

        while (changed && attempts < MaxAttempts)
        {
            changed = false;

            foreach (JsonNode candidate in Smaller(current))
            {
                if (++attempts > MaxAttempts)
                {
                    break;
                }

                if (fails(candidate.ToJsonString()))
                {
                    current = candidate;
                    changed = true;
                    break;
                }
            }
        }

        return current.ToJsonString();
    }

    private static bool Fails(ReferenceFailure failure, string scenario, string document, string second, JsonSchemaImportDialect dialect, ReferenceVariant variant, Func<ReferenceCase, JsonSchemaOptions, EmitResult>? emit)
    {
        ReferenceCase? @case = ReferenceCase.Build(scenario, document, second, dialect);

        if (@case is null)
        {
            return false;
        }

        JsonElement[] instances = failure.Instance is null ? [] : [JsonElement.Parse(failure.Instance)];
        return ReferenceChecker.Check(@case, variant, instances, emit is null ? null : options => emit(@case, options)).Any(found => found.Kind == failure.Kind);
    }

    /// <summary>Every document with one member or entry taken out, largest parts first.</summary>
    private static IEnumerable<JsonNode> Smaller(JsonNode root)
    {
        List<(JsonNode Parent, object Key, int Size)> parts = [];
        Collect(root, parts);

        foreach ((JsonNode parent, object key, int _) in parts.OrderByDescending(part => part.Size))
        {
            JsonNode copy = root.DeepClone();
            JsonNode? twin = Twin(root, copy, parent);

            if (twin is JsonObject members)
            {
                members.Remove((string)key);
            }
            else if (twin is JsonArray items)
            {
                items.RemoveAt((int)key);
            }

            yield return copy;
        }
    }

    private static void Collect(JsonNode node, List<(JsonNode Parent, object Key, int Size)> parts)
    {
        if (node is JsonObject members)
        {
            foreach ((string name, JsonNode? value) in members)
            {
                parts.Add((node, name, value?.ToJsonString().Length ?? 4));

                if (value is not null)
                {
                    Collect(value, parts);
                }
            }
        }
        else if (node is JsonArray items)
        {
            for (int index = 0; index < items.Count; index++)
            {
                parts.Add((node, index, items[index]?.ToJsonString().Length ?? 4));

                if (items[index] is { } value)
                {
                    Collect(value, parts);
                }
            }
        }
    }

    private static JsonNode? Twin(JsonNode original, JsonNode copy, JsonNode target)
    {
        JsonNode? current = copy;

        foreach (object step in Steps(original, target))
        {
            current = step is string name ? current?[name] : current?[(int)step];
        }

        return current;
    }

    private static Stack<object> Steps(JsonNode original, JsonNode target)
    {
        Stack<object> steps = new();

        for (JsonNode node = target; !ReferenceEquals(node, original); node = node.Parent!)
        {
            steps.Push(node.Parent is JsonArray ? node.GetElementIndex() : node.GetPropertyName());
        }

        return steps;
    }

    /// <summary>
    /// The keywords of a document, with names and indexes blanked, and the same for each <c>$ref</c>, so that two documents that
    /// differ only in naming count as one shape.
    /// </summary>
    public static string Shape(string document)
    {
        StringBuilder text = new();
        Write(JsonNode.Parse(document), text, isMap: false);
        return text.ToString();
    }

    private static void Write(JsonNode? node, StringBuilder text, bool isMap)
    {
        if (node is JsonObject members)
        {
            text.Append('{');

            foreach ((string keyword, JsonNode? value) in members.OrderBy(member => isMap ? "*" : member.Key, StringComparer.Ordinal))
            {
                text.Append(isMap ? "*" : keyword).Append(':');

                if (keyword == "$ref" && !isMap)
                {
                    text.Append(ReferenceShape(value?.ToString() ?? string.Empty));
                }
                else if (keyword is "$id" or "id" or "$anchor" && !isMap)
                {
                    text.Append(value?.ToString().Contains('#', StringComparison.Ordinal) == true ? "frag" : "id");
                }
                else if (keyword is "properties" or "patternProperties" or "$defs" or "definitions" or "dependentSchemas" or "dependencies" && !isMap)
                {
                    Write(value, text, isMap: true);
                }
                else if (value is JsonObject or JsonArray && keyword is not ("enum" or "const" or "required"))
                {
                    Write(value, text, isMap: false);
                }
                else
                {
                    text.Append('_');
                }

                text.Append(',');
            }

            text.Append('}');
        }
        else if (node is JsonArray items)
        {
            text.Append('[');

            foreach (JsonNode? item in items)
            {
                Write(item, text, isMap: false);
                text.Append(',');
            }

            text.Append(']');
        }
        else
        {
            text.Append('_');
        }
    }

    private static string ReferenceShape(string reference) => reference.StartsWith("#/", StringComparison.Ordinal)
        ? string.Join('/', reference.Split('/').Select(token => token.Length > 0 && token.All(char.IsAsciiDigit) ? "N" : IsKeyword(Uri.UnescapeDataString(token)) ? Uri.UnescapeDataString(token) : "*"))
        : reference is "#" ? "#" : "#anchor";

    private static bool IsKeyword(string token) =>
        token is "#" or "$defs" or "definitions" or "properties" or "patternProperties" or "dependentSchemas" or "dependencies" or "allOf" or "anyOf" or "oneOf" or "items"
            or "prefixItems" or "additionalItems" or "not" or "if" or "then" or "else" or "contains" or "additionalProperties" or "propertyNames" or "contentSchema" or "x-stash";
}

/// <summary>Collects failures across threads, by document, so that a report can state how many documents in 1,000 fail each way.</summary>
internal sealed class ReferenceReport(bool shrink)
{
    private const int ShrunkPerGroup = 2;

    private readonly ConcurrentDictionary<(string Kind, string Profile), ConcurrentDictionary<int, byte>> _documents = new();
    private readonly ConcurrentDictionary<(string Kind, string Profile, string Variant, string Scenario), int> _groups = new();
    private readonly ConcurrentDictionary<string, (int Count, string Example)> _shapes = new(StringComparer.Ordinal);

    public ReferenceCounters Counters { get; } = new();

    public int Documents => Counters["documents judged"];

    /// <summary>The documents that failed each way. <see cref="ReferenceChecker.OracleLimit"/> is reported, but isn't a failure.</summary>
    public IEnumerable<(string Kind, string Profile, int Documents)> Failing =>
        _documents.Where(entry => entry.Key.Kind != ReferenceChecker.OracleLimit).Select(entry => (entry.Key.Kind, entry.Key.Profile, entry.Value.Count));

    /// <summary>A failure of several documents together, which has no one document to shrink.</summary>
    public void AddTogether(int iteration, string kind, string detail, string document)
    {
        _documents.GetOrAdd((kind, "together"), _ => new())[iteration] = 0;
        _shapes.AddOrUpdate($"{kind} | together | {ReferenceShrinker.Shape(document)}", (1, $"iteration {iteration}: {document} ({detail})"), (_, known) => (known.Count + 1, known.Example));
    }

    /// <summary>Import didn't end. The profile doesn't matter, so it's counted once, under no profile.</summary>
    public void AddRunaway(int iteration, string document)
    {
        _documents.GetOrAdd(("d-import-runaway", "any"), _ => new())[iteration] = 0;
        _shapes.AddOrUpdate($"d-import-runaway | any | {ReferenceShrinker.Shape(document)}", (1, $"iteration {iteration}: {document}"), (_, known) => (known.Count + 1, known.Example));
    }

    public void Add(int iteration, ReferenceCase @case, ReferenceVariant variant, ReferenceFailure failure)
    {
        _documents.GetOrAdd((failure.Kind, failure.Profile), _ => new())[iteration] = 0;
        int seen = _groups.AddOrUpdate((failure.Kind, failure.Profile, failure.Variant, @case.Scenario), 1, (_, count) => count + 1);

        if (!shrink || seen > ShrunkPerGroup)
        {
            return;
        }

        (string shrunk, string second) = ReferenceShrinker.Shrink(failure, @case, variant);
        string shape = $"{failure.Kind} | {failure.Profile} | {ReferenceShrinker.Shape(shrunk)} + {ReferenceShrinker.Shape(second)}";
        string example = $"iteration {iteration} {failure.Profile}/{failure.Variant} {@case.Scenario} {@case.Dialect}: {shrunk} + {second}{(failure.Instance is null ? string.Empty : " instance " + failure.Instance)} ({failure.Detail})";
        _shapes.AddOrUpdate(shape, (1, example), (_, known) => (known.Count + 1, known.Example));
    }

    public string Describe()
    {
        int documents = Math.Max(1, Documents);
        StringBuilder text = new();
        text.AppendLine(CultureInfo.InvariantCulture, $"Documents judged: {Documents}");
        text.AppendLine("== Failing documents per 1,000, by kind and profile");

        foreach (((string kind, string profile), ConcurrentDictionary<int, byte> failing) in _documents.OrderBy(entry => entry.Key.Kind, StringComparer.Ordinal).ThenBy(entry => entry.Key.Profile, StringComparer.Ordinal))
        {
            int shapes = _shapes.Keys.Count(shape => shape.StartsWith($"{kind} | {profile} |", StringComparison.Ordinal));
            text.AppendLine(CultureInfo.InvariantCulture, $"{kind,-32} {profile,-14} {failing.Count * 1000.0 / documents,8:F1}  ({failing.Count} documents, {shapes} shapes)");
        }

        text.AppendLine("== Failures by kind and variant");

        foreach (var group in _groups.GroupBy(entry => (entry.Key.Kind, entry.Key.Variant)).OrderBy(group => group.Key.Kind, StringComparer.Ordinal).ThenBy(group => group.Key.Variant, StringComparer.Ordinal))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{group.Key.Kind,-32} {group.Key.Variant,-24} {group.Sum(entry => entry.Value)}");
        }

        text.AppendLine("== Failures by kind and scenario");

        foreach (var group in _groups.GroupBy(entry => (entry.Key.Kind, entry.Key.Scenario)).OrderBy(group => group.Key.Kind, StringComparer.Ordinal).ThenBy(group => group.Key.Scenario, StringComparer.Ordinal))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{group.Key.Kind,-32} {group.Key.Scenario,-24} {group.Sum(entry => entry.Value)}");
        }

        text.AppendLine("== Counters");

        foreach ((string name, int count) in Counters.All)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{name}: {count}");
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"== Shapes, shrunk ({_shapes.Count}; at most {ShrunkPerGroup} shrunk per kind, profile, variant and scenario)");

        foreach ((string shape, (int count, string example)) in _shapes.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"[{count}] {shape}");
            text.AppendLine(CultureInfo.InvariantCulture, $"    {example}");
        }

        return text.ToString();
    }
}
