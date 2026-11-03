using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zorg.Json.Emission;

namespace Zorg.Json.Llm;

/// <summary>Emits schemas for providers, checks them and reads the answers.</summary>
public static class LlmSchemaExtensions
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { AllowDuplicateProperties = false };

    /// <summary>Emits the schema to send to a provider. Zod equivalent: <c>z.toJSONSchema</c> with an SDK's own transform on top.</summary>
    /// <typeparam name="T">The type of the parsed value.</typeparam>
    /// <param name="schema">The schema.</param>
    /// <param name="profile">The profile, such as <see cref="LlmProfiles.OpenAIStrict"/>.</param>
    /// <returns>The schema and its diagnostics. Keep it for <see cref="ParseModelOutput{T}(Schema{T}, EmitResult, string)"/>.</returns>
    /// <example>
    /// <code>
    /// EmitResult emitted = schema.Emit(LlmProfiles.AnthropicStrict);
    /// </code>
    /// </example>
    public static EmitResult Emit<T>(this Schema<T> schema, JsonSchemaProfile profile)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return schema.Emit(new JsonSchemaOptions { Target = profile });
    }

    /// <summary>
    /// Reports what a provider will refuse or won't enforce, against the profile's table. Keyword locations point into
    /// the schema emitted as Draft 2020-12. Limits that a provider counts per request are checked for this schema alone.
    /// </summary>
    /// <typeparam name="T">The type of the parsed value.</typeparam>
    /// <param name="schema">The schema.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="capabilities">The table to use instead of the profile's own. Required for a profile from outside this package.</param>
    /// <returns>The report.</returns>
    /// <exception cref="ArgumentException">The profile has no table and <paramref name="capabilities"/> is null.</exception>
    /// <example>
    /// <code>
    /// CompatibilityReport report = schema.CheckCompatibility(LlmProfiles.OpenAIStrict);
    /// if (!report.IsCompatible)
    /// {
    ///     throw new InvalidOperationException(report.Issues[0].Message);
    /// }
    /// </code>
    /// </example>
    public static CompatibilityReport CheckCompatibility<T>(this Schema<T> schema, JsonSchemaProfile profile, SchemaCapabilities? capabilities = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(profile);

        SchemaCapabilities table = capabilities
            ?? profile.PostProcessors.OfType<CapabilityEnforcer>().FirstOrDefault()?.Capabilities
            ?? throw new ArgumentException("This profile has no capability table, so pass one.", nameof(capabilities));

        bool wrapped = profile.Rewriters.Contains(Rewriters.RequireObjectRoot) && !Rewriters.HasObjectRoot(schema.ToNode());
        return new CompatibilityChecker(table, profile.Name).Check(schema.Emit(), schema.Emit(profile), wrapped);
    }

    /// <summary>
    /// Validates a model's answer against the full schema, after undoing what the profile changed: a wrapped root is
    /// unwrapped, and <c>null</c> is removed for a property that was optional and doesn't accept <c>null</c>, so a
    /// <c>Default</c> then applies. A property that's both optional and nullable keeps its <c>null</c>.
    /// Zod equivalent: <c>safeParse</c> on the original schema.
    /// </summary>
    /// <typeparam name="T">The type of the parsed value.</typeparam>
    /// <param name="schema">The schema that was emitted.</param>
    /// <param name="emitted">The result of emitting it.</param>
    /// <param name="json">The model's JSON text.</param>
    /// <returns>The value, or the issues. Malformed JSON is an <see cref="IssueCodes.InvalidJson"/> issue.</returns>
    /// <example>
    /// <code>
    /// EmitResult emitted = schema.Emit(LlmProfiles.OpenAIStrict);
    /// ParseResult&lt;JsonElement&gt; answer = schema.ParseModelOutput(emitted, responseText);
    /// </code>
    /// </example>
    public static ParseResult<T> ParseModelOutput<T>(this Schema<T> schema, EmitResult emitted, string json)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            return schema.ParseModelOutput(emitted, JsonElement.Parse(json, DocumentOptions));
        }
        catch (JsonException)
        {
            return schema.SafeParse(json);
        }
    }

    /// <summary>Validates a model's UTF-8 answer against the full schema, as the <see cref="string"/> overload does.</summary>
    /// <typeparam name="T">The type of the parsed value.</typeparam>
    /// <param name="schema">The schema that was emitted.</param>
    /// <param name="emitted">The result of emitting it.</param>
    /// <param name="utf8Json">The model's UTF-8 encoded JSON.</param>
    /// <returns>The value, or the issues. Malformed JSON is an <see cref="IssueCodes.InvalidJson"/> issue.</returns>
    /// <example>
    /// <code>
    /// ParseResult&lt;JsonElement&gt; answer = schema.ParseModelOutput(emitted, responseUtf8);
    /// </code>
    /// </example>
    public static ParseResult<T> ParseModelOutput<T>(this Schema<T> schema, EmitResult emitted, ReadOnlySpan<byte> utf8Json)
    {
        ArgumentNullException.ThrowIfNull(schema);

        try
        {
            return schema.ParseModelOutput(emitted, JsonElement.Parse(utf8Json, DocumentOptions));
        }
        catch (JsonException)
        {
            return schema.SafeParse(utf8Json);
        }
    }

    /// <summary>Validates a parsed answer against the full schema, as the <see cref="string"/> overload does.</summary>
    /// <typeparam name="T">The type of the parsed value.</typeparam>
    /// <param name="schema">The schema that was emitted.</param>
    /// <param name="emitted">The result of emitting it.</param>
    /// <param name="json">The element. Duplicate properties can't be detected at this point.</param>
    /// <returns>The value, or the issues.</returns>
    /// <example>
    /// <code>
    /// ParseResult&lt;JsonElement&gt; answer = schema.ParseModelOutput(emitted, document.RootElement);
    /// </code>
    /// </example>
    public static ParseResult<T> ParseModelOutput<T>(this Schema<T> schema, EmitResult emitted, JsonElement json)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(emitted);
        return schema.SafeParse(ModelOutputAdapter.Adapt(schema.ToNode(), emitted, json));
    }

    /// <summary>
    /// Copies a schema's top-level keywords into the shape the Anthropic SDK takes for <c>JsonOutputFormat.Schema</c>
    /// and <c>Tool.InputSchema</c>.
    /// </summary>
    /// <param name="schema">An emitted schema.</param>
    /// <returns>A new dictionary, independent of <paramref name="schema"/>.</returns>
    /// <example>
    /// <code>
    /// var format = new JsonOutputFormat { Schema = emitted.Schema.ToJsonElementDictionary() };
    /// </code>
    /// </example>
    public static Dictionary<string, JsonElement> ToJsonElementDictionary(this JsonObject schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return schema.ToDictionary(entry => entry.Key, entry => JsonElement.Parse(entry.Value?.ToJsonString() ?? "null"), StringComparer.Ordinal);
    }
}
