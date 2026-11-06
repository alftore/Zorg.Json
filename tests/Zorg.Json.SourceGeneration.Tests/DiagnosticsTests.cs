using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Zorg.Json.SourceGeneration.Tests;

/// <summary>Runs the generator on source text, to see what it reports and that what it writes compiles.</summary>
public sealed class DiagnosticsTests
{
    private const string Usings = """
        using System.ComponentModel.DataAnnotations;
        using System.Text.Json.Serialization;
        using Zorg.Json;

        """;

    private static (ImmutableArray<Diagnostic> Generator, ImmutableArray<Diagnostic> Compiler) Run(string source)
    {
        MetadataReference[] references = [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator)
            .Where(path => path.EndsWith(".dll", StringComparison.Ordinal))
            .Select(path => MetadataReference.CreateFromFile(path))];

        CSharpParseOptions parsing = new(LanguageVersion.Preview);
        CSharpCompilation compilation = CSharpCompilation.Create(
            "Test",
            [CSharpSyntaxTree.ParseText(Usings + source, parsing)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create([new SchemaGenerator().AsSourceGenerator()], parseOptions: parsing);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out ImmutableArray<Diagnostic> diagnostics);
        return (diagnostics, [.. output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)]);
    }

    private const string Context = """

        [JsonSerializable(typeof(P))]
        public partial class C : JsonSerializerContext;
        """;

    [Theory]
    [InlineData("[GenerateSchema(typeof(C))] public record P(string A);", "ZORG0001")]
    [InlineData("[GenerateSchema(typeof(C))] public partial record P(string A) { public static int Schema => 1; }", "ZORG0003")]
    [InlineData("[GenerateSchema(typeof(string))] public partial record P(string A);", "ZORG0004")]
    [InlineData("[GenerateSchema(typeof(C))] public partial record P([property: Range(typeof(System.DateTime), \"2000-01-01\", \"2001-01-01\")] System.DateTime A);", "ZORG0005")]
    [InlineData("[GenerateSchema(typeof(C))] public partial record P([property: CreditCard] string A);", "ZORG0005")]
    [InlineData("[GenerateSchema(typeof(C))] public partial record P([property: MinLength(1)] int A);", "ZORG0005")]
    [InlineData("[GenerateSchema(typeof(C))] public partial record P([property: Range(1, 2)] string A);", "ZORG0005")]
    [InlineData("[GenerateSchema(typeof(C))] public partial class P { [Required] public string? A { get; set; } }", "ZORG0006")]
    public void A_mistake_is_reported(string source, string id)
    {
        (ImmutableArray<Diagnostic> found, _) = Run(source + Context);

        Assert.Contains(found, diagnostic => diagnostic.Id == id);
    }

    [Fact]
    public void A_nested_type_is_refused()
    {
        (ImmutableArray<Diagnostic> found, _) = Run("""
            public partial class Outer { [GenerateSchema(typeof(C))] public partial record P(string A); }
            [JsonSerializable(typeof(Outer.P))]
            public partial class C : JsonSerializerContext;
            """);

        Assert.Contains(found, diagnostic => diagnostic.Id == "ZORG0002");
    }

    [Fact]
    public void A_type_the_context_does_not_list_is_reported()
    {
        (ImmutableArray<Diagnostic> found, _) = Run("""
            [GenerateSchema(typeof(C))] public partial record P(string A);
            public partial class C : JsonSerializerContext;
            """);

        Assert.Contains(found, diagnostic => diagnostic.Id == "ZORG0004");
    }

    [Theory]
    [InlineData("public partial record P([property: MinLength(1), Required] string A, [property: Range(0.5, 2.5)] int B, [property: Range(1, 2)] decimal C2, [property: Range(1, 2)] float? D, [property: Range(1, 2)] Zorg.Json.Optional<long> E);")]
    [InlineData("public partial record struct P([property: MaxLength(3)] string A);")]
    [InlineData("public partial class P { [MinLength(1)] public string[] A { get; set; } = []; [MinLength(1)] public Zorg.Json.Optional<string> B { get; set; } }")]
    [InlineData("public partial record P([property: System.ComponentModel.Description(\"quote \\\" and \\\\ back\")] string A);")]
    [InlineData("public partial record P([property: RegularExpression(\"\\\\d+\\\"\")] string A);")]
    public void What_is_written_compiles(string source)
    {
        (_, ImmutableArray<Diagnostic> errors) = Run("[GenerateSchema(typeof(C))] " + source + Context);

        // The JSON context comes from a generator this run doesn't have, so its Default is missing and its abstract members aren't written.
        Assert.Empty(errors.Where(error => error.Id is not ("CS0117" or "CS0534" or "CS7036")));
    }
}
