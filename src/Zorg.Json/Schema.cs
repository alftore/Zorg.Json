using System;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Json.Emission;
using Zorg.Json.Model;
using Zorg.Json.Validation;

namespace Zorg.Json;

/// <summary>
/// An immutable, thread-safe schema that parses JSON into <typeparamref name="T"/> and emits JSON Schema.
/// Store it in a <see langword="static readonly"/> field. Create one with <see cref="Z"/>.
/// </summary>
/// <typeparam name="T">The type of the parsed value.</typeparam>
public abstract class Schema<T>
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { AllowDuplicateProperties = false };

    private ValidationPlan? _plan;

    private protected Schema(SchemaNode node) => Node = node;

    private protected SchemaNode Node { get; }

    private ValidationPlan Plan => _plan ??= new ValidationPlan(Node);

    /// <summary>Gets the keyword model behind this schema.</summary>
    /// <returns>The node.</returns>
    public SchemaNode ToNode() => Node;

    /// <summary>Validates JSON text and returns a result instead of throwing. Zod equivalent: <c>safeParse</c>.</summary>
    /// <param name="json">The JSON text.</param>
    /// <returns>The value, or the issues. Malformed JSON is an <see cref="IssueCodes.InvalidJson"/> issue.</returns>
    /// <example>
    /// <code>
    /// ParseResult&lt;string&gt; result = Z.String().MinLength(1).SafeParse("\"hello\"");
    /// </code>
    /// </example>
    public ParseResult<T> SafeParse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            return SafeParse(JsonElement.Parse(json, DocumentOptions));
        }
        catch (JsonException exception)
        {
            return InvalidJson(exception);
        }
    }

    /// <summary>Validates UTF-8 JSON and returns a result instead of throwing. Zod equivalent: <c>safeParse</c>.</summary>
    /// <param name="utf8Json">The UTF-8 encoded JSON.</param>
    /// <returns>The value, or the issues. Malformed JSON is an <see cref="IssueCodes.InvalidJson"/> issue.</returns>
    /// <example>
    /// <code>
    /// ParseResult&lt;string&gt; result = Z.String().SafeParse("\"hello\""u8);
    /// </code>
    /// </example>
    public ParseResult<T> SafeParse(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            return SafeParse(JsonElement.Parse(utf8Json, DocumentOptions));
        }
        catch (JsonException exception)
        {
            return InvalidJson(exception);
        }
    }

    /// <summary>Validates an already parsed element and returns a result instead of throwing. Zod equivalent: <c>safeParse</c>.</summary>
    /// <param name="json">The element. Duplicate properties can't be detected at this point.</param>
    /// <returns>The value, or the issues.</returns>
    /// <example>
    /// <code>
    /// ParseResult&lt;string&gt; result = Z.String().SafeParse(document.RootElement);
    /// </code>
    /// </example>
    public ParseResult<T> SafeParse(JsonElement json)
    {
        ValidationContext context = new();
        JsonElement value = Plan.Run(json, context);

        if (context.HasIssues)
        {
            return ParseResult<T>.Failure(context.ToIssues());
        }

        T result = Materialize(value);

        foreach (SchemaRefinement refinement in Node.Behavior.Refinements)
        {
            if (!((SchemaRefinement<T>)refinement).Predicate(result))
            {
                context.ReportCustom(refinement.Message);
            }
        }

        return context.HasIssues ? ParseResult<T>.Failure(context.ToIssues()) : ParseResult<T>.Success(result);
    }

    /// <summary>Reads and validates a UTF-8 JSON stream. Zod equivalent: <c>safeParseAsync</c>.</summary>
    /// <param name="utf8Json">The stream, which is read to the end and left open.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The value, or the issues. Malformed JSON is an <see cref="IssueCodes.InvalidJson"/> issue.</returns>
    /// <example>
    /// <code>
    /// ParseResult&lt;string&gt; result = await schema.SafeParseAsync(request.Body, cancellationToken);
    /// </code>
    /// </example>
    public async ValueTask<ParseResult<T>> SafeParseAsync(Stream utf8Json, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(utf8Json);

        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(utf8Json, DocumentOptions, cancellationToken).ConfigureAwait(false);
            return SafeParse(document.RootElement);
        }
        catch (JsonException exception)
        {
            return InvalidJson(exception);
        }
    }

    /// <summary>Validates JSON text and throws if it's invalid. Zod equivalent: <c>parse</c>.</summary>
    /// <param name="json">The JSON text.</param>
    /// <returns>The value.</returns>
    /// <exception cref="SchemaValidationException">The input is invalid.</exception>
    /// <example>
    /// <code>
    /// string name = Z.String().MinLength(1).Parse("\"hello\"");
    /// </code>
    /// </example>
    public T Parse(string json) => ValueOrThrow(SafeParse(json));

    /// <summary>Validates UTF-8 JSON and throws if it's invalid. Zod equivalent: <c>parse</c>.</summary>
    /// <param name="utf8Json">The UTF-8 encoded JSON.</param>
    /// <returns>The value.</returns>
    /// <exception cref="SchemaValidationException">The input is invalid.</exception>
    /// <example>
    /// <code>
    /// string name = Z.String().Parse("\"hello\""u8);
    /// </code>
    /// </example>
    public T Parse(ReadOnlySpan<byte> utf8Json) => ValueOrThrow(SafeParse(utf8Json));

    /// <summary>Validates an already parsed element and throws if it's invalid. Zod equivalent: <c>parse</c>.</summary>
    /// <param name="json">The element.</param>
    /// <returns>The value.</returns>
    /// <exception cref="SchemaValidationException">The input is invalid.</exception>
    /// <example>
    /// <code>
    /// string name = Z.String().Parse(document.RootElement);
    /// </code>
    /// </example>
    public T Parse(JsonElement json) => ValueOrThrow(SafeParse(json));

    /// <summary>Validates JSON text in the <c>TryParse</c> style.</summary>
    /// <param name="json">The JSON text.</param>
    /// <param name="value">The value, when the input is valid.</param>
    /// <param name="issues">The reasons for failure. Empty on success.</param>
    /// <returns>Whether the input is valid.</returns>
    /// <example>
    /// <code>
    /// if (schema.TryParse(json, out string? name, out ImmutableArray&lt;ParseIssue&gt; issues))
    /// {
    ///     Console.WriteLine(name);
    /// }
    /// </code>
    /// </example>
    public bool TryParse(string json, [MaybeNullWhen(false)] out T value, out ImmutableArray<ParseIssue> issues)
    {
        ParseResult<T> result = SafeParse(json);
        value = result.IsSuccess ? result.Value : default;
        issues = result.Issues;
        return result.IsSuccess;
    }

    /// <summary>
    /// Emits JSON Schema, by default Draft 2020-12 describing the input. Zod equivalent: <c>z.toJSONSchema</c>,
    /// except that Zod describes the output by default.
    /// </summary>
    /// <param name="options">Target, direction and policy. Null uses the defaults.</param>
    /// <returns>A new JSON Schema document.</returns>
    /// <remarks>
    /// The input schema accepts everything <see cref="SafeParse(string)"/> accepts. It's only ever looser, where
    /// C# rules can't be expressed. Use <see cref="Emit"/> to see what was left out.
    /// </remarks>
    /// <example>
    /// <code>
    /// JsonObject schema = Z.String().MinLength(1).ToJsonSchema();
    /// // { "$schema": "https://json-schema.org/draft/2020-12/schema", "type": "string", "minLength": 1 }
    /// </code>
    /// </example>
    public JsonObject ToJsonSchema(JsonSchemaOptions? options = null) => Emit(options).Schema;

    /// <summary>Emits JSON Schema together with diagnostics about anything it couldn't express.</summary>
    /// <param name="options">Target, direction and policy. Null uses the defaults.</param>
    /// <returns>The schema and its diagnostics.</returns>
    /// <exception cref="InvalidOperationException">
    /// Something can't be expressed and <see cref="JsonSchemaOptions.Unrepresentable"/> is <see cref="UnrepresentablePolicy.Throw"/>.
    /// </exception>
    /// <example>
    /// <code>
    /// EmitResult result = Z.String().Trim().MaxLength(3).Emit();
    /// foreach (EmitDiagnostic diagnostic in result.Diagnostics)
    /// {
    ///     Console.WriteLine(diagnostic.Message);
    /// }
    /// </code>
    /// </example>
    public EmitResult Emit(JsonSchemaOptions? options = null) => JsonSchemaEmitter.Emit(Node, options ?? new JsonSchemaOptions());

    private protected abstract T Materialize(JsonElement value);

    private static T ValueOrThrow(ParseResult<T> result) =>
        result.IsSuccess ? result.Value : throw new SchemaValidationException(result.Issues);

    private static ParseResult<T> InvalidJson(JsonException exception)
    {
        ValidationContext context = new();
        context.ReportInvalidJson(exception);
        return ParseResult<T>.Failure(context.ToIssues());
    }
}

/// <summary>Adds the fluent members every schema shares, keeping the concrete builder type through a chain.</summary>
/// <typeparam name="T">The type of the parsed value.</typeparam>
/// <typeparam name="TSelf">The concrete builder.</typeparam>
public abstract class Schema<T, TSelf> : Schema<T>
    where TSelf : Schema<T, TSelf>
{
    private protected Schema(SchemaNode node)
        : base(node)
    {
    }

    /// <summary>Sets a short name. JSON Schema: <c>title</c>. Zod equivalent: <c>.meta({ title })</c>.</summary>
    /// <param name="title">The title.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// StringSchema schema = Z.String().Title("Name");
    /// </code>
    /// </example>
    [Pure]
    public TSelf Title(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        return With(Node with { Title = title });
    }

    /// <summary>Sets an explanation. JSON Schema: <c>description</c>. Zod equivalent: <c>.meta({ description })</c>.</summary>
    /// <param name="description">The description.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// StringSchema schema = Z.String().Description("Search text");
    /// </code>
    /// </example>
    [Pure]
    public TSelf Description(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        return With(Node with { Description = description });
    }

    /// <summary>Alias of <see cref="Description"/>. JSON Schema: <c>description</c>. Zod equivalent: <c>.describe()</c>.</summary>
    /// <param name="description">The description.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// StringSchema schema = Z.String().Describe("Search text");
    /// </code>
    /// </example>
    [Pure]
    public TSelf Describe(string description) => Description(description);

    /// <summary>
    /// Adds a C# check that runs after the constraints have passed. JSON Schema can't express it, so see
    /// <see cref="JsonSchemaOptions.Unrepresentable"/>. Zod equivalent: <c>.refine()</c>.
    /// </summary>
    /// <param name="predicate">Returns true when the value is valid.</param>
    /// <param name="message">The issue message. The issue's code is <see cref="IssueCodes.Custom"/>.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// StringSchema schema = Z.String().Refine(text => text != "admin", "This name is reserved");
    /// </code>
    /// </example>
    [Pure]
    public TSelf Refine(Func<T, bool> predicate, string message)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(message);

        return With(Node with
        {
            Behavior = Node.Behavior with { Refinements = Node.Behavior.Refinements.Add(new SchemaRefinement<T>(predicate, message)) },
        });
    }

    private protected abstract TSelf With(SchemaNode node);
}
