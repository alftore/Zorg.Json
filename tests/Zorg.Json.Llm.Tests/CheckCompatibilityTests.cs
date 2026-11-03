using System;
using System.Linq;
using System.Text.Json;
using Xunit;
using Zorg.Json.Emission;

namespace Zorg.Json.Llm.Tests;

public sealed class CheckCompatibilityTests
{
    [Fact]
    public void Order_is_compatible_with_OpenAI()
    {
        CompatibilityReport report = Samples.Order.CheckCompatibility(LlmProfiles.OpenAIStrict);

        Assert.True(report.IsCompatible, string.Join("\n", report.Issues.Select(issue => issue.Message)));
        Assert.Equal("openai-strict", report.ProfileName);
    }

    [Fact]
    public void Report_for_OpenAI() => Snapshot.Match(Text(Samples.Order.CheckCompatibility(LlmProfiles.OpenAIStrict)), "txt");

    [Fact]
    public void Report_for_Anthropic() => Snapshot.Match(Text(Samples.Order.CheckCompatibility(LlmProfiles.AnthropicStrict)), "txt");

    [Fact]
    public void Report_for_Gemini() => Snapshot.Match(Text(Samples.Order.CheckCompatibility(LlmProfiles.GeminiJsonSchema)), "txt");

    [Fact]
    public void Relocated_keyword_has_a_location_in_the_full_schema()
    {
        CompatibilityReport report = Samples.Order.CheckCompatibility(LlmProfiles.OpenAIStrict);

        CompatibilityIssue issue = Assert.Single(report.Issues, candidate => candidate.Keyword == "maxLength");
        Assert.Equal(CompatibilitySeverity.Warning, issue.Severity);
        Assert.Equal("/properties/customer/properties/name", issue.Location);
    }

    [Fact]
    public void Recursion_is_an_error_for_Anthropic()
    {
        CompatibilityReport report = Samples.Category.CheckCompatibility(LlmProfiles.AnthropicStrict);

        Assert.False(report.IsCompatible);
        Assert.Contains(report.Issues, issue => issue.Kind == CompatibilityIssueKind.Recursion);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("gemini")]
    public void Recursion_is_fine_elsewhere(string provider)
    {
        JsonSchemaProfile profile = provider == "openai" ? LlmProfiles.OpenAIStrict : LlmProfiles.GeminiJsonSchema;

        Assert.DoesNotContain(Samples.Category.CheckCompatibility(profile).Issues, issue => issue.Kind == CompatibilityIssueKind.Recursion);
    }

    [Fact]
    public void Map_is_an_error_for_closed_object_providers()
    {
        CompatibilityReport report = Z.Record(Z.Integer()).CheckCompatibility(LlmProfiles.OpenAIStrict);

        Assert.False(report.IsCompatible);
        Assert.Contains(report.Issues, issue => issue.Kind == CompatibilityIssueKind.OpenObject);
    }

    [Fact]
    public void Map_is_fine_for_Gemini() =>
        Assert.True(Z.Record(Z.Integer()).CheckCompatibility(LlmProfiles.GeminiJsonSchema).IsCompatible);

    [Fact]
    public void Refinement_is_a_warning()
    {
        CompatibilityIssue issue = Assert.Single(
            Samples.Order.CheckCompatibility(LlmProfiles.OpenAIStrict).Issues,
            candidate => candidate.Kind == CompatibilityIssueKind.Refinement);

        Assert.Equal(CompatibilitySeverity.Warning, issue.Severity);
        Assert.Equal("/properties/code", issue.Location);
    }

    [Fact]
    public void Wrapped_root_is_info()
    {
        CompatibilityIssue issue = Assert.Single(
            Z.Array(Z.String()).CheckCompatibility(LlmProfiles.OpenAIStrict).Issues,
            candidate => candidate.Kind == CompatibilityIssueKind.Root);

        Assert.Equal(CompatibilitySeverity.Info, issue.Severity);
    }

    [Fact]
    public void Plain_profile_needing_an_object_root_is_an_error()
    {
        CompatibilityReport report = Z.Array(Z.String()).CheckCompatibility(JsonSchemaProfile.Draft202012, OpenAICapabilities.AsOf2026_11);

        Assert.Contains(report.Issues, issue => issue.Kind == CompatibilityIssueKind.Root && issue.Severity == CompatibilitySeverity.Error);
    }

    [Fact]
    public void Plain_profile_shows_residual_keywords_and_open_objects()
    {
        ObjectSchema schema = Z.Object().Passthrough().Property("a", Z.String().MinLength(1).Optional());

        CompatibilityReport report = schema.CheckCompatibility(JsonSchemaProfile.Draft202012, OpenAICapabilities.AsOf2026_11);

        Assert.Contains(report.Issues, issue => issue.Kind == CompatibilityIssueKind.UnsupportedKeyword && issue.Keyword == "minLength" && issue.Location == "/properties/a");
        Assert.Contains(report.Issues, issue => issue.Kind == CompatibilityIssueKind.OpenObject && issue.Severity == CompatibilitySeverity.Error);
        Assert.Contains(report.Issues, issue => issue.Kind == CompatibilityIssueKind.OptionalProperty && issue.Severity == CompatibilitySeverity.Error);
    }

    [Fact]
    public void Profile_without_a_table_needs_one() =>
        Assert.Throws<ArgumentException>(() => Z.String().CheckCompatibility(JsonSchemaProfile.Draft202012));

    [Fact]
    public void Nesting_limit()
    {
        ISchema schema = Z.String();

        for (int level = 0; level < 11; level++)
        {
            schema = Z.Object().Property("next", schema);
        }

        CompatibilityReport report = ((ObjectSchema)schema).CheckCompatibility(LlmProfiles.OpenAIStrict);

        Assert.Contains(report.Issues, issue => issue.Kind == CompatibilityIssueKind.LimitExceeded && issue.Message.Contains("levels of nesting"));
    }

    [Fact]
    public void Ten_levels_of_nesting_are_allowed()
    {
        ISchema schema = Z.String();

        for (int level = 0; level < 10; level++)
        {
            schema = Z.Object().Property("next", schema);
        }

        Assert.True(((ObjectSchema)schema).CheckCompatibility(LlmProfiles.OpenAIStrict).IsCompatible);
    }

    [Fact]
    public void Property_count_limit()
    {
        ObjectSchema schema = Z.Object().Property("a", Z.String()).Property("b", Z.String());

        CompatibilityReport report = schema.CheckCompatibility(LlmProfiles.OpenAIStrict, OpenAICapabilities.AsOf2026_11 with { MaxProperties = 1 });

        Assert.Contains(report.Issues, issue => issue.Kind == CompatibilityIssueKind.LimitExceeded && issue.Keyword == "properties");
    }

    [Fact]
    public void Enum_count_limit()
    {
        CompatibilityReport report = Z.Object().Property("a", Z.String().Enum("x", "y"))
            .CheckCompatibility(LlmProfiles.OpenAIStrict, OpenAICapabilities.AsOf2026_11 with { MaxEnumValues = 1 });

        Assert.Contains(report.Issues, issue => issue.Kind == CompatibilityIssueKind.LimitExceeded && issue.Keyword == "enum");
    }

    [Fact]
    public void Long_enum_limit()
    {
        CompatibilityReport report = Z.Object().Property("a", Z.String().Enum("xx", "yy", "zz"))
            .CheckCompatibility(LlmProfiles.OpenAIStrict, OpenAICapabilities.AsOf2026_11 with { LongEnumThreshold = 2, MaxLongEnumLength = 5 });

        Assert.Contains(report.Issues, issue => issue.Kind == CompatibilityIssueKind.LimitExceeded && issue.Location == "/properties/a");
    }

    [Fact]
    public void Text_limit()
    {
        CompatibilityReport report = Z.Object().Property("abcdef", Z.String())
            .CheckCompatibility(LlmProfiles.OpenAIStrict, OpenAICapabilities.AsOf2026_11 with { MaxTotalStringLength = 5 });

        Assert.Contains(report.Issues, issue => issue.Kind == CompatibilityIssueKind.LimitExceeded && issue.Message.Contains("characters"));
    }

    [Fact]
    public void Anthropic_counts_optional_properties()
    {
        ObjectSchema schema = Z.Object();

        for (int index = 0; index < 25; index++)
        {
            schema = schema.Property($"p{index}", Z.String().Optional());
        }

        CompatibilityReport report = schema.CheckCompatibility(LlmProfiles.AnthropicStrict);

        Assert.Contains(report.Issues, issue => issue.Kind == CompatibilityIssueKind.LimitExceeded && issue.Keyword == "required");
    }

    [Fact]
    public void Anthropic_counts_union_properties()
    {
        ObjectSchema schema = Z.Object();

        for (int index = 0; index < 17; index++)
        {
            schema = schema.Property($"p{index}", Z.String().Nullable());
        }

        CompatibilityReport report = schema.CheckCompatibility(LlmProfiles.AnthropicStrict);

        Assert.Contains(report.Issues, issue => issue.Kind == CompatibilityIssueKind.LimitExceeded && issue.Keyword == "anyOf");
    }

    [Theory]
    [InlineData(@"\bword", "word boundary")]
    [InlineData(@"(?=a)b", "lookahead")]
    [InlineData(@"(?<!a)b", "lookbehind")]
    [InlineData(@"(a)\1", "backreference")]
    public void Anthropic_relocates_unsupported_regex_features(string pattern, string feature)
    {
        ObjectSchema schema = Z.Object().Property("a", Z.String().Pattern(pattern));

        string json = schema.Emit(LlmProfiles.AnthropicStrict).Schema.ToJsonString();

        Assert.False(json.Contains("\"pattern\"", StringComparison.Ordinal), feature);
        Assert.Contains(schema.CheckCompatibility(LlmProfiles.AnthropicStrict).Issues, issue => issue.Keyword == "pattern" && issue.Severity == CompatibilitySeverity.Warning);
    }

    [Theory]
    [InlineData("^[a-z]+$")]
    [InlineData(@"[\b]x")]
    [InlineData(@"\\b")]
    [InlineData(@"(?<year>\d+)")]
    [InlineData(@"(?:a|b){1,3}")]
    public void Anthropic_keeps_simple_patterns(string pattern)
    {
        string json = Z.Object().Property("a", Z.String().Pattern(pattern)).Emit(LlmProfiles.AnthropicStrict).Schema.ToJsonString();

        Assert.Contains("\"pattern\"", json);
    }

    [Fact]
    public void Large_quantifier_is_a_warning_and_stays()
    {
        ObjectSchema schema = Z.Object().Property("a", Z.String().Pattern("^a{1,1000}$"));

        CompatibilityIssue issue = Assert.Single(
            schema.CheckCompatibility(LlmProfiles.AnthropicStrict).Issues,
            candidate => candidate.Kind == CompatibilityIssueKind.RegexComplexity);

        Assert.Equal(CompatibilitySeverity.Warning, issue.Severity);
        Assert.Contains("\"pattern\"", schema.Emit(LlmProfiles.AnthropicStrict).Schema.ToJsonString());
    }

    [Fact]
    public void Format_outside_the_table_moves_to_description()
    {
        ObjectSchema schema = Z.Object().Property("site", Z.String().Uri());

        string openAI = schema.Emit(LlmProfiles.OpenAIStrict).Schema["properties"]!["site"]!.ToJsonString();
        string anthropic = schema.Emit(LlmProfiles.AnthropicStrict).Schema["properties"]!["site"]!.ToJsonString();

        Assert.Equal("""{"description":"Must also satisfy: format uri.","type":"string"}""", openAI);
        Assert.Equal("""{"type":"string","format":"uri"}""", anthropic);
    }

    [Fact]
    public void Number_size_formats_disappear_without_a_note()
    {
        EmitResult emitted = Z.Object().Property("n", Z.Integer()).Emit(LlmProfiles.OpenAIStrict);

        Assert.Equal("""{"type":"integer"}""", emitted.Schema["properties"]!["n"]!.ToJsonString());
        Assert.Contains(emitted.Diagnostics, diagnostic => diagnostic.Keyword == "format" && diagnostic.Location == "/properties/n");
    }

    [Fact]
    public void Anthropic_keeps_small_min_items_only()
    {
        ObjectSchema schema = Z.Object()
            .Property("a", Z.Array(Z.String()).MinItems(1))
            .Property("b", Z.Array(Z.String()).MinItems(2));

        JsonSchemaOptions options = new() { Target = LlmProfiles.AnthropicStrict };
        var properties = schema.ToJsonSchema(options)["properties"]!;

        Assert.Equal(1, properties["a"]!["minItems"]!.GetValue<int>());
        Assert.Null(properties["b"]!["minItems"]);
        Assert.Equal("Must also satisfy: minItems 2.", properties["b"]!["description"]!.GetValue<string>());
    }

    [Fact]
    public void Untyped_schema_is_a_warning_for_strict_providers()
    {
        CompatibilityReport report = Z.Object().Property("a", Z.Any()).CheckCompatibility(LlmProfiles.OpenAIStrict);

        Assert.Contains(report.Issues, issue => issue.Kind == CompatibilityIssueKind.MissingType && issue.Severity == CompatibilitySeverity.Warning);
    }

    [Fact]
    public void Table_records_its_source()
    {
        Assert.Equal(new DateOnly(2026, 11, 3), OpenAICapabilities.AsOf2026_11.AsOf);
        Assert.Equal("Anthropic", AnthropicCapabilities.AsOf2026_11.Provider);
        Assert.StartsWith("https://", GeminiCapabilities.JsonSchemaAsOf2026_11.Source.ToString());
        Assert.Contains("multipleOf", GeminiCapabilities.OpenApiAsOf2026_11.UnsupportedKeywords);
    }

    [Fact]
    public void Json_element_dictionary_matches_the_schema()
    {
        EmitResult emitted = Z.Object().Property("a", Z.String()).Emit(LlmProfiles.AnthropicStrict);

        var dictionary = emitted.Schema.ToJsonElementDictionary();

        Assert.Equal("object", dictionary["type"].GetString());
        Assert.Equal(JsonValueKind.False, dictionary["additionalProperties"].ValueKind);
        Assert.Equal("string", dictionary["properties"].GetProperty("a").GetProperty("type").GetString());
    }

    private static string Text(CompatibilityReport report) => string.Join(
        '\n',
        report.Issues.Select(issue => $"{issue.Severity} {issue.Kind} {issue.Keyword} @{issue.Location}: {issue.Message}"));
}
