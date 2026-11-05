using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Zorg.Json.Sampling;

namespace Zorg.Json.ComplianceTests.Scale;

/// <summary>One schema shared by many threads, with first use racing. Results must equal a single-threaded run.</summary>
public sealed class ConcurrencyTests
{
    private const int Threads = 16;
    private const int Trials = 12;

    private const string ImportedDocument = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$defs": {
            "node": {
              "type": "object",
              "properties": {
                "name": { "type": "string", "minLength": 1, "pattern": "^[a-z][a-z0-9-]*$" },
                "kids": { "type": "array", "items": { "$ref": "#/$defs/node" }, "maxItems": 4 },
                "kind": { "enum": ["leaf", "branch"] },
                "when": { "type": "string", "format": "date" }
              },
              "required": ["name"],
              "patternProperties": { "^x-": { "type": "number" } },
              "unevaluatedProperties": false
            }
          },
          "oneOf": [
            { "$ref": "#/$defs/node" },
            { "type": "array", "items": { "$ref": "#/$defs/node" }, "uniqueItems": true },
            { "type": "integer", "multipleOf": 3, "minimum": 0 }
          ],
          "if": { "type": "object" },
          "then": { "minProperties": 1 }
        }
        """;

    private static readonly string[] ImportedCorpus =
    [
        """{"name":"a"}""", """{"name":"A"}""", """{"name":"a","kids":[{"name":"b"},{"name":"c","kids":[{"name":"d"}]}]}""",
        """{"name":"a","kids":[{"name":1}]}""", """{"name":"a","kids":[1,2,3,4,5]}""", """{"name":"a","x-1":1}""", """{"name":"a","x-1":"s"}""",
        """{"name":"a","other":1}""", """{"name":"a","kind":"leaf","when":"2026-11-05"}""", """{"name":"a","when":"2026-13-45"}""", "{}",
        """[{"name":"a"},{"name":"b"}]""", """[{"name":"a"},{"name":"a"}]""", "9", "10", "-3", "3.0", "1e2", "null", "\"s\"", "{", "",
        """{"name":"a","kids":[{"name":"b","kids":[{"name":"c","kids":[{"name":"d","kids":[{"name":"e","kids":[{"name":"f"}]}]}]}]}]}""",
    ];

    private static readonly string[] TreeCorpus =
    [
        """{"name":"a","children":[]}""", """{"name":"a","children":[{"name":"b","children":[]}]}""", """{"name":"","children":[]}""",
        """{"name":"a"}""", """{"name":"a","children":[{"name":"b","children":[{"name":"c","children":[{"name":"d","children":[{"name":"","children":[]}]}]}]}]}""",
        """{"name":"a","children":null}""", """{"name":"a","children":[],"extra":1}""", "[]", "null", "{",
        """{"name":"a","children":[{"name":"b","children":[{"name":"c","children":[]}]},{"name":"d","children":[]}]}""",
    ];

    private static readonly string[] FluentCorpus =
    [
        """{"v":1,"next":null}""", """{"v":1,"next":{"v":2,"next":null}}""", """{"v":1,"next":{"v":"x","next":null}}""", """{"v":1}""",
        """{"v":1,"next":{"v":2,"next":{"v":3,"next":{"v":4,"next":{"v":5,"next":null}}}}}""", "null", "{}", """{"v":1,"next":null,"z":1}""", "[1]", "",
    ];

    private sealed record Target(Func<string, string> Parse, Func<string> Emit, Func<string> Sample);

    private static Target Describe<T>(Schema<T> schema, Func<T, string> show) => new(
        document =>
        {
            ParseResult<T> result = schema.SafeParse(document);

            return result.IsSuccess
                ? "ok " + show(result.Value)
                : string.Join(';', result.Issues.Select(issue => $"{issue.Code}@{issue.InstanceLocation}#{issue.Keyword}"));
        },
        () => schema.ToJsonSchema().ToJsonString() + schema.ToJsonSchema(new() { Direction = Emission.SchemaDirection.Output }).ToJsonString(),
        () =>
        {
            SampleResult sample = schema.GenerateSample();
            return sample.IsValid + sample.Value.GetRawText();
        });

    private static Target Imported() => Describe(Z.FromJsonSchema(ImportedDocument), element => element.GetRawText());

    private static Target TypedTree()
    {
        ObjectSchema<Category>? schema = null;

        schema = Zorg.Json.Schema.Object(ScaleJson.Default.Category)
            .Property(category => category.Name, name => name.MinLength(1))
            .Property(category => category.Children, children => children.Items(Zorg.Json.Schema.Lazy<Category>("category", () => schema!)));

        return Describe(schema, category => JsonSerializer.Serialize(category, ScaleJson.Default.Category));
    }

    private static Target FluentChain()
    {
        ISchema? chain = null;
        JsonValueSchema lazy = Z.Lazy("chain", () => chain!);

        chain = Z.Object().Property("v", Z.Integer().Minimum(0)).Property("next", Z.AnyOf(Z.Null(), lazy).Optional()).Property("w", Z.String().Pattern("^a+$").Optional());

        return Describe(lazy, element => element.GetRawText());
    }

    private static Target Union() => Describe(
        Z.DiscriminatedUnion(
            "kind",
            Z.Object().Property("kind", Z.String().Const("a")).Property("n", Z.Integer().Coerce().Default(1)),
            Z.Object().Property("kind", Z.String().Const("b")).Property("s", Z.String().Trim().MinLength(1)).Strip()),
        element => element.GetRawText());

    private static readonly string[] UnionCorpus =
    [
        """{"kind":"a"}""", """{"kind":"a","n":"7"}""", """{"kind":"b","s":"  x "}""", """{"kind":"b","s":"  "}""", """{"kind":"c"}""", "{}", """{"kind":"b","s":"x","z":1}""", "1",
    ];

    public static TheoryData<string> Cases { get; } = ["imported", "typed tree", "fluent chain", "union"];

    private static (Func<Target> Factory, string[] Corpus) Case(string name) => name switch
    {
        "imported" => (Imported, ImportedCorpus),
        "typed tree" => (TypedTree, TreeCorpus),
        "fluent chain" => (FluentChain, FluentCorpus),
        _ => (Union, UnionCorpus),
    };

    private static (string[] Parses, string Emit, string Sample) Baseline(Target target, string[] corpus) =>
        ([.. corpus.Select(target.Parse)], target.Emit(), target.Sample());

    [Theory]
    [MemberData(nameof(Cases))]
    public void First_use_racing_gives_the_single_threaded_results(string name)
    {
        (Func<Target> factory, string[] corpus) = Case(name);
        (string[] parses, string emit, string sample) = Baseline(factory(), corpus);

        for (int trial = 0; trial < Trials; trial++)
        {
            Target shared = factory();
            using Barrier start = new(Threads);
            List<string> mismatches = [];

            Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, worker =>
            {
                start.SignalAndWait();

                for (int step = 0; step < corpus.Length; step++)
                {
                    int index = (step + (worker * 3)) % corpus.Length;
                    string actual = shared.Parse(corpus[index]);

                    if (actual != parses[index])
                    {
                        lock (mismatches)
                        {
                            mismatches.Add($"{name} trial {trial} doc {corpus[index]}: {actual} != {parses[index]}");
                        }
                    }

                    if (worker % 4 == 1 && step == 2 && shared.Emit() != emit)
                    {
                        lock (mismatches)
                        {
                            mismatches.Add($"{name} trial {trial}: emitted schema differs");
                        }
                    }

                    if (worker % 4 == 2 && step == 3 && shared.Sample() != sample)
                    {
                        lock (mismatches)
                        {
                            mismatches.Add($"{name} trial {trial}: sample differs");
                        }
                    }
                }
            });

            Assert.Empty(mismatches);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Emit_and_sample_racing_first_give_the_single_threaded_results(string name)
    {
        (Func<Target> factory, string[] corpus) = Case(name);
        (string[] parses, string emit, string sample) = Baseline(factory(), corpus);

        for (int trial = 0; trial < Trials; trial++)
        {
            Target shared = factory();
            using Barrier start = new(Threads);

            string[] emitted = new string[Threads];
            string[] sampled = new string[Threads];

            Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, worker =>
            {
                start.SignalAndWait();
                emitted[worker] = shared.Emit();
                sampled[worker] = shared.Sample();
            });

            Assert.All(emitted, value => Assert.Equal(emit, value));
            Assert.All(sampled, value => Assert.Equal(sample, value));
            Assert.Equal(parses, corpus.Select(shared.Parse));
        }
    }

    [Fact]
    public async Task Async_parsing_shares_a_schema_too()
    {
        ObjectSchema schema = Z.Object().Property("name", Z.String().MinLength(1).RefineAsync(static (name, _) => ValueTask.FromResult(name != "bad"), "no")).Property("n", Z.Integer());
        string[] documents = ["""{"name":"ok","n":1}""", """{"name":"bad","n":1}""", """{"name":"","n":1}""", """{"name":"ok"}"""];
        bool[] expected = [true, false, false, false];

        bool[][] results = await Task.WhenAll(Enumerable.Range(0, 32).Select(worker => Task.Run(async () =>
        {
            bool[] outcome = new bool[documents.Length];

            for (int index = 0; index < documents.Length; index++)
            {
                using System.IO.MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes(documents[(index + worker) % documents.Length]));
                outcome[(index + worker) % documents.Length] = (await schema.SafeParseAsync(stream)).IsSuccess;
            }

            return outcome;
        })));

        Assert.All(results, outcome => Assert.Equal(expected, outcome));
    }
}
