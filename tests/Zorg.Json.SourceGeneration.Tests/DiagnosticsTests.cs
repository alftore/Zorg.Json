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

    [Theory]
    [InlineData("[JsonDerivedType(typeof(D), \"d\")] public abstract partial record P(string A); public record D(string A) : P(A);")]
    [InlineData("[JsonConverter(typeof(JsonConverter<P>))] public partial record P(string A);")]
    public void A_type_whose_json_is_its_own_is_refused(string source)
    {
        (ImmutableArray<Diagnostic> found, _) = Run("[GenerateSchema(typeof(C))] " + source + Context);

        Assert.Contains(found, diagnostic => diagnostic.Id == "ZORG0007");
    }

    [Theory]
    [InlineData("\\\\A\\\\d+\\\\z")]
    [InlineData("(?i)abc")]
    [InlineData("[")]
    public void A_pattern_only_dotnet_reads_is_reported_and_left_out(string pattern)
    {
        (ImmutableArray<Diagnostic> found, ImmutableArray<Diagnostic> errors) = Run($"[GenerateSchema(typeof(C))] public partial record P([property: RegularExpression(\"{pattern}\")] string A);" + Context);

        Assert.Contains(found, diagnostic => diagnostic.Id == "ZORG0005");
        Assert.Empty(errors.Where(error => error.Id is not ("CS0117" or "CS0534" or "CS7036")));
    }

    [Theory]
    [InlineData("[Range(300, 400)] byte")]
    [InlineData("[Range(-5, -1)] uint")]
    public void A_range_nothing_can_meet_is_reported(string property)
    {
        (ImmutableArray<Diagnostic> found, _) = Run("[GenerateSchema(typeof(C))] public partial class P { " + property + " A { get; set; } }" + Context);

        Assert.Contains(found, diagnostic => diagnostic.Id == "ZORG0005");
    }

    [Theory]
    [InlineData("public partial class P { [JsonInclude, MinLength(1)] public string A = \"\"; }")]
    [InlineData("public partial class P { [JsonInclude, MinLength(1)] internal string B { get; set; } = \"\"; }")]
    [InlineData("[CustomValidation(typeof(string), \"M\")] public partial class P;")]
    [InlineData("public partial class P { public P([MinLength(1)] string a) => A = a; public string A { get; } }")]
    [InlineData("public partial class P { [JsonConverter(typeof(JsonConverter<string>)), MinLength(1)] public string? A { get; set; } }")]
    [InlineData("public partial class P { public System.Collections.Generic.List<string?>? A { get; set; } }")]
    public void A_rule_nothing_applies_is_reported(string source)
    {
        (ImmutableArray<Diagnostic> found, _) = Run("[GenerateSchema(typeof(C))] " + source + Context);

        Assert.Contains(found, diagnostic => diagnostic.Id == "ZORG0005");
    }

    [Theory]
    [InlineData("public partial class P { [Required] public required string? A { get; set; } }", true)]
    [InlineData("public partial record P([property: Required] string? A);", true)]
    [InlineData("public partial record P([property: Required] string A = \"x\");", true)]
    [InlineData("public partial class P { [JsonConstructor] public P() { } public P(string a) { } [Required] public string A { get; set; } = \"\"; }", true)]
    [InlineData("public partial class P { [Required, JsonRequired] public string A { get; set; } = \"\"; }", false)]
    [InlineData("public partial record P([property: Required] string A);", false)]
    [InlineData("public partial record struct P([property: Required] string A);", true)]
    [InlineData("public partial struct P { public P(string a) { A = a; } [Required] public string A { get; } }", true)]
    public void Required_that_can_not_hold_is_reported(string source, bool reported)
    {
        (ImmutableArray<Diagnostic> found, _) = Run("[GenerateSchema(typeof(C))] " + source + Context);

        Assert.Equal(reported, found.Any(diagnostic => diagnostic.Id == "ZORG0006"));
    }

    [Theory]
    [InlineData("public class B { [MaxLength(3)] public string Schema { get; set; } = \"\"; } [GenerateSchema(typeof(C))] public partial class P : B;", "ZORG0003")]
    [InlineData("[GenerateSchema(typeof(C))] public partial class P : IValidatableObject { public System.Collections.Generic.IEnumerable<ValidationResult> Validate(ValidationContext c) => []; }", "ZORG0005")]
    [InlineData("[GenerateSchema(typeof(C))] public partial record P([property: EmailAddress, Url] string A);", "ZORG0005")]
    [InlineData("[GenerateSchema(typeof(C))] public partial record P([property: Range(1e300, 1e301)] float A);", "ZORG0005")]
    public void More_mistakes_are_reported(string source, string id)
    {
        (ImmutableArray<Diagnostic> found, _) = Run(source + Context);

        Assert.Contains(found, diagnostic => diagnostic.Id == id);
    }

    [Fact]
    public void A_private_schema_on_a_base_needs_no_new_and_no_length_means_no_warning()
    {
        (ImmutableArray<Diagnostic> found, ImmutableArray<Diagnostic> errors) = Run("public class B { private static int Schema = 0; public static int Get() => Schema; } [GenerateSchema(typeof(C))] public partial record P2([property: MaxLength] string A);" + """

            [JsonSerializable(typeof(P2))]
            public partial class C : JsonSerializerContext;
            """);

        Assert.DoesNotContain(found, diagnostic => diagnostic.Id == "ZORG0005");
        Assert.Empty(errors.Where(error => error.Id is not ("CS0117" or "CS0534" or "CS7036")));
    }

    [Fact]
    public void A_type_the_context_reaches_through_a_property_is_known()
    {
        (ImmutableArray<Diagnostic> found, _) = Run("""
            [GenerateSchema(typeof(C))] public partial record N(string A);
            public record W(N Inner);
            [JsonSerializable(typeof(W))]
            public partial class C : JsonSerializerContext;
            """);

        Assert.DoesNotContain(found, diagnostic => diagnostic.Id == "ZORG0004");
    }

    [Theory]
    [InlineData("public record W { [JsonIgnore] public N? I { get; set; } }", true)]
    [InlineData("public record W { public static N? I { get; set; } }", true)]
    [InlineData("public record W { private N? I { get; set; } }", true)]
    [InlineData("public record B { public N? I { get; set; } } public record W : B;", false)]
    [InlineData("public class W : System.Collections.Generic.List<N>;", false)]
    public void Only_what_the_serializer_reads_counts_as_known(string source, bool reported)
    {
        (ImmutableArray<Diagnostic> found, _) = Run("[GenerateSchema(typeof(C))] public partial record N(string A);\n" + source + """

            [JsonSerializable(typeof(W))]
            public partial class C : JsonSerializerContext;
            """);

        Assert.Equal(reported, found.Any(diagnostic => diagnostic.Id == "ZORG0004"));
    }

    [Theory]
    [InlineData("public class B { public class Schema { } }")]
    [InlineData("public class B { protected int Schema { get; set; } }")]
    [InlineData("public class B { public string Schema() => string.Empty; }")]
    [InlineData("public class B { [JsonIgnore] public int Schema { get; set; } }")]
    public void A_member_of_a_base_that_is_no_json_property_is_only_hidden(string source)
    {
        (ImmutableArray<Diagnostic> found, ImmutableArray<Diagnostic> errors) = Run(source + " [GenerateSchema(typeof(C))] public partial class P : B;" + Context);

        Assert.DoesNotContain(found, diagnostic => diagnostic.Id == "ZORG0003");
        Assert.Empty(errors.Where(error => error.Id is not ("CS0117" or "CS0534" or "CS7036")));
    }

    [Fact]
    public void A_type_named_schema_is_reported()
    {
        (ImmutableArray<Diagnostic> found, _) = Run("""
            [GenerateSchema(typeof(C))] public partial record Schema(string A);
            [JsonSerializable(typeof(Schema))]
            public partial class C : JsonSerializerContext;
            """);

        Assert.Contains(found, diagnostic => diagnostic.Id == "ZORG0003");
    }

    [Fact]
    public void One_awkward_type_does_not_stop_the_others()
    {
        (ImmutableArray<Diagnostic> found, ImmutableArray<Diagnostic> errors) = Run("""
            namespace @class.Foo
            {
                [GenerateSchema(typeof(C))] public partial record Foo(string A);
                [GenerateSchema(typeof(C))] public partial record FOO(string A);
                [JsonSerializable(typeof(Foo))]
                [JsonSerializable(typeof(FOO))]
                public partial class C : JsonSerializerContext;
            }
            """);

        Assert.DoesNotContain(found, diagnostic => diagnostic.Id == "CS8785");
        Assert.Empty(errors.Where(error => error.Id is not ("CS0117" or "CS0534" or "CS7036")));
    }

    [Fact]
    public void A_type_from_a_different_context_is_not_used_as_a_nested_schema()
    {
        const string Source = """
            [GenerateSchema(typeof(C1))] public partial record A(B Inner);
            [GenerateSchema(typeof(C2))] public partial record B(string V);
            [JsonSerializable(typeof(A))] public partial class C1 : JsonSerializerContext;
            [JsonSerializable(typeof(B))] public partial class C2 : JsonSerializerContext;
            """;
        (_, ImmutableArray<Diagnostic> errors) = Run(Source);

        Assert.Empty(errors.Where(error => error.Id is not ("CS0117" or "CS0534" or "CS7036")));
    }

    [Fact]
    public void A_warning_can_be_turned_off_where_it_is()
    {
        (ImmutableArray<Diagnostic> found, _) = Run("[GenerateSchema(typeof(C))] public partial record P([property: CreditCard] string A);" + Context);

        Assert.True(found.Single(diagnostic => diagnostic.Id == "ZORG0005").Location.IsInSource);
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
