using System;
using System.Collections.Generic;
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
using Zorg.Json.Sampling;
using Zorg.Json.Typed;
using Zorg.Json.Validation;

namespace Zorg.Json;

/// <summary>
/// An immutable, thread-safe schema that parses JSON into <typeparamref name="T"/> and emits JSON Schema.
/// Store it in a <see langword="static readonly"/> field. Create one with <see cref="Z"/>.
/// </summary>
/// <typeparam name="T">The type of the parsed value.</typeparam>
public abstract class Schema<T> : ISchema, ISampleTarget
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { AllowDuplicateProperties = false };

    private static readonly JsonElement NullElement = JsonElement.Parse("null");

    private ValidationPlan? _plan;
    private bool? _hasOutputOnly;

    private protected Schema(SchemaNode node) => Node = node;

    private protected SchemaNode Node { get; }

    private ValidationPlan Plan => _plan ??= ValidationPlan.Create(Node);

    /// <inheritdoc/>
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
        JsonElement element;

        try
        {
            element = JsonElement.Parse(json, DocumentOptions);
        }
        catch (Exception exception) when (IsUnreadable(exception))
        {
            return InvalidJson(exception);
        }

        return SafeParse(element);
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
        JsonElement element;

        try
        {
            element = JsonElement.Parse(utf8Json, DocumentOptions);
        }
        catch (Exception exception) when (IsUnreadable(exception))
        {
            return InvalidJson(exception);
        }

        return SafeParse(element);
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
        if (json.ValueKind == JsonValueKind.Undefined)
        {
            return InvalidJson(new JsonException("The element has no value."));
        }

        ValidationContext context = new(json);
        JsonElement value = Plan.Run(json, context);

        if (context.HasPendingRefinements)
        {
            throw new InvalidOperationException("This schema has async refinements. Use SafeParseAsync.");
        }

        return Finish(value, context);
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
    public ValueTask<ParseResult<T>> SafeParseAsync(Stream utf8Json, CancellationToken cancellationToken = default) =>
        SafeParseAsync(utf8Json, null, cancellationToken);

    // The callback gets the element after the pipeline, while the document is still alive.
    internal async ValueTask<ParseResult<T>> SafeParseAsync(Stream utf8Json, Action<JsonElement>? onValidated, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(utf8Json);

        JsonDocument document;

        try
        {
            document = await JsonDocument.ParseAsync(utf8Json, DocumentOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsUnreadable(exception))
        {
            return InvalidJson(exception);
        }

        using (document)
        {
            return await SafeParseAsync(document.RootElement, onValidated, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Validates an element, running async refinements. Zod equivalent: <c>safeParseAsync</c>.</summary>
    /// <param name="json">The element.</param>
    /// <param name="cancellationToken">Passed to the async refinements.</param>
    /// <returns>The value, or the issues.</returns>
    /// <example>
    /// <code>
    /// ParseResult&lt;string&gt; result = await schema.SafeParseAsync(document.RootElement, cancellationToken);
    /// </code>
    /// </example>
    public ValueTask<ParseResult<T>> SafeParseAsync(JsonElement json, CancellationToken cancellationToken = default) =>
        SafeParseAsync(json, null, cancellationToken);

    private async ValueTask<ParseResult<T>> SafeParseAsync(JsonElement json, Action<JsonElement>? onValidated, CancellationToken cancellationToken)
    {
        if (json.ValueKind == JsonValueKind.Undefined)
        {
            return InvalidJson(new JsonException("The element has no value."));
        }

        ValidationContext context = new(json);
        JsonElement value = Plan.Run(json, context);

        if (!context.HasIssues)
        {
            await context.RunPendingAsync(cancellationToken).ConfigureAwait(false);
        }

        ParseResult<T> result = Finish(value, context);

        if (result.IsSuccess)
        {
            onValidated?.Invoke(value);
        }

        return result;
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
    /// Validates an object you already have, such as an MVC model or a bound options instance, by serializing it and
    /// running the same pipeline as <see cref="SafeParse(JsonElement)"/>. Zod equivalent: <c>safeParse</c> on an existing value.
    /// </summary>
    /// <param name="instance">The object to check. It isn't changed.</param>
    /// <returns>
    /// On success, the value after the pipeline: normalizers such as <c>Trim</c> have run and defaults are filled in.
    /// For a reference type that is a new instance, equal to <paramref name="instance"/> only when nothing was changed.
    /// On failure, the issues, with locations as in the serialized JSON. A <see langword="null"/> is validated as
    /// JSON <c>null</c>, so it fails unless the schema allows it. If the serializer can't write the object, the result is an
    /// <see cref="IssueCodes.DeserializationFailed"/> issue with the exception text in <c>Parameters["detail"]</c>.
    /// </returns>
    /// <remarks>
    /// Properties that are only written, such as computed ones, are removed from the JSON before it's checked, because
    /// parsing never accepts them. Refinements run on the converted value. A schema with <c>RefineAsync</c> needs
    /// <see cref="ValidateAsync"/>.
    /// </remarks>
    /// <example>
    /// <code>
    /// ParseResult&lt;Person&gt; result = PersonSchema.Validate(model);
    ///
    /// if (!result.IsSuccess)
    /// {
    ///     return ValidationProblem(result.Issues.ToDictionary());
    /// }
    /// </code>
    /// </example>
    public ParseResult<T> Validate(T instance)
    {
        JsonElement element;

        try
        {
            element = Serialize(instance);
        }
        catch (Exception exception) when (Deserialization.Failed(exception))
        {
            return SerializationFailed(exception);
        }

        return SafeParse(element);
    }

    /// <summary>Validates an object you already have, running async refinements. See <see cref="Validate"/>.</summary>
    /// <param name="instance">The object to check. It isn't changed.</param>
    /// <param name="cancellationToken">Passed to the async refinements.</param>
    /// <returns>The value after the pipeline, or the issues.</returns>
    /// <example>
    /// <code>
    /// ParseResult&lt;Person&gt; result = await PersonSchema.ValidateAsync(model, cancellationToken);
    /// </code>
    /// </example>
    public ValueTask<ParseResult<T>> ValidateAsync(T instance, CancellationToken cancellationToken = default)
    {
        JsonElement element;

        try
        {
            element = Serialize(instance);
        }
        catch (Exception exception) when (Deserialization.Failed(exception))
        {
            return ValueTask.FromResult(SerializationFailed(exception));
        }

        return SafeParseAsync(element, cancellationToken);
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

    internal abstract T Materialize(JsonElement value);

    internal abstract JsonElement ToElement(T value);

    ImmutableArray<ParseIssue> ISampleTarget.FinishSample(JsonElement value, ValidationContext context) => Finish(value, context).Issues;

    private ParseResult<T> Finish(JsonElement value, ValidationContext context)
    {
        if (context.HasIssues)
        {
            return ParseResult<T>.Failure(context.ToIssues());
        }

        try
        {
            return ParseResult<T>.Success(Materialize(value));
        }
        catch (Exception exception) when (Deserialization.Failed(exception))
        {
            context.ReportDeserializationFailed(exception);
            return ParseResult<T>.Failure(context.ToIssues());
        }
    }

    private JsonElement Serialize(T instance)
    {
        if (instance is null)
        {
            return NullElement;
        }

        JsonElement element = ToElement(instance);
        _hasOutputOnly ??= OutputOnlyProperties.Any(Node);
        return _hasOutputOnly.Value ? OutputOnlyProperties.Remove(element, Node) : element;
    }

    private static ParseResult<T> SerializationFailed(Exception exception)
    {
        ValidationContext context = new();
        context.ReportDeserializationFailed(exception);
        return ParseResult<T>.Failure(context.ToIssues());
    }

    private static T ValueOrThrow(ParseResult<T> result) =>
        result.IsSuccess ? result.Value : throw new SchemaValidationException(result.Issues);

    /// <summary>Besides malformed JSON, the parser throws these for invalid UTF-16 and for undecodable property names.</summary>
    private static bool IsUnreadable(Exception exception) =>
        exception is JsonException or InvalidOperationException or ArgumentException;

    private static ParseResult<T> InvalidJson(Exception exception)
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

    /// <summary>Records why this exists. JSON Schema: <c>$comment</c>.</summary>
    /// <param name="comment">The comment.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// StringSchema schema = Z.String().Comment("Kept for older clients");
    /// </code>
    /// </example>
    [Pure]
    public TSelf Comment(string comment)
    {
        ArgumentNullException.ThrowIfNull(comment);
        return With(Node with { Comment = comment });
    }

    /// <summary>Marks the value as deprecated. JSON Schema: <c>deprecated</c>. Zod equivalent: <c>.meta({ deprecated: true })</c>.</summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// StringSchema schema = Z.String().Deprecated();
    /// </code>
    /// </example>
    [Pure]
    public TSelf Deprecated() => With(Node with { Deprecated = true });

    /// <summary>Marks the value as read-only. JSON Schema: <c>readOnly</c>. It's only an annotation.</summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// StringSchema schema = Z.String().ReadOnly();
    /// </code>
    /// </example>
    [Pure]
    public TSelf ReadOnly() => With(Node with { ReadOnly = true });

    /// <summary>Marks the value as write-only. JSON Schema: <c>writeOnly</c>. It's only an annotation.</summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// StringSchema schema = Z.String().WriteOnly();
    /// </code>
    /// </example>
    [Pure]
    public TSelf WriteOnly() => With(Node with { WriteOnly = true });

    /// <summary>Adds example values. JSON Schema: <c>examples</c>. Zod equivalent: <c>.meta({ examples })</c>.</summary>
    /// <param name="examples">The examples.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// StringSchema schema = Z.String().Examples("Ada", "Grace");
    /// </code>
    /// </example>
    [Pure]
    public TSelf Examples(params ReadOnlySpan<T> examples)
    {
        ImmutableArray<JsonElement>.Builder elements = ImmutableArray.CreateBuilder<JsonElement>(examples.Length);

        foreach (T example in examples)
        {
            elements.Add(ToElement(example));
        }

        return With(Node with { Examples = Node.Examples.AddRange(elements) });
    }

    /// <summary>Adds a custom keyword to the emitted schema, such as <c>x-order</c>. Zod equivalent: <c>.meta()</c>.</summary>
    /// <param name="keyword">The keyword.</param>
    /// <param name="value">Its value.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// StringSchema schema = Z.String().Meta("x-order", JsonElement.Parse("1"));
    /// </code>
    /// </example>
    [Pure]
    public TSelf Meta(string keyword, JsonElement value)
    {
        ArgumentNullException.ThrowIfNull(keyword);
        return With(Node with { Extensions = Node.Extensions.Add(KeyValuePair.Create(keyword, value.Clone())) });
    }

    /// <summary>
    /// Names this schema, so that it's written once under <c>$defs</c> when
    /// <see cref="JsonSchemaOptions.Reused"/> is <see cref="ReusedSchemaHandling.Ref"/>. Zod equivalent: <c>.meta({ id })</c>.
    /// </summary>
    /// <param name="name">The name under <c>$defs</c>.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// StringSchema email = Z.String().Email().Named("email");
    /// </code>
    /// </example>
    [Pure]
    public TSelf Named(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return With(Node with { Behavior = Node.Behavior with { DefinitionName = name } });
    }

    /// <summary>
    /// Lets the property be absent, when used as an object property. No keyword of its own: the property is left out of
    /// <c>required</c>. Zod equivalent: <c>.optional()</c>.
    /// </summary>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// ObjectSchema schema = Z.Object().Property("nick", Z.String().Optional());
    /// </code>
    /// </example>
    [Pure]
    public TSelf Optional() => With(Node with { Behavior = Node.Behavior with { IsOptional = true } });

    /// <summary>
    /// Fills in this value when the property is absent. JSON Schema: <c>default</c>, and the property is left out of
    /// <c>required</c> in the input schema. Zod equivalent: <c>.default()</c>.
    /// </summary>
    /// <param name="value">The default value.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// ObjectSchema schema = Z.Object().Property("limit", Z.Integer().Default(10));
    /// </code>
    /// </example>
    [Pure]
    public TSelf Default(T value) => With(Node with
    {
        Default = ToElement(value),
        Behavior = Node.Behavior with { IsOptional = true, FillDefault = true },
    });

    /// <summary>
    /// Adds a C# check that runs after the constraints have passed. JSON Schema can't express it, so see
    /// <see cref="JsonSchemaOptions.Unrepresentable"/>. Zod equivalent: <c>.refine()</c>.
    /// </summary>
    /// <param name="predicate">Returns true when the value is valid.</param>
    /// <param name="message">The issue message. The issue's code is <see cref="IssueCodes.Custom"/>.</param>
    /// <param name="hint">Replaces the message in the emitted schema.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// StringSchema schema = Z.String().Refine(text => text != "admin", "This name is reserved");
    /// </code>
    /// </example>
    [Pure]
    public TSelf Refine(Func<T, bool> predicate, string message, string? hint = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(message);
        return AddRefinement(new SchemaRefinement<T>(Materialize, predicate, null, message, string.Empty, hint));
    }

    /// <summary>
    /// Adds an async C# check, such as a database lookup. The schema then has to be parsed with <c>SafeParseAsync</c>.
    /// Zod equivalent: an async <c>.refine()</c>.
    /// </summary>
    /// <param name="predicate">Returns true when the value is valid.</param>
    /// <param name="message">The issue message. The issue's code is <see cref="IssueCodes.Custom"/>.</param>
    /// <param name="hint">Replaces the message in the emitted schema.</param>
    /// <returns>A new schema.</returns>
    /// <example>
    /// <code>
    /// StringSchema schema = Z.String().RefineAsync(users.IsFreeAsync, "This name is taken");
    /// </code>
    /// </example>
    [Pure]
    public TSelf RefineAsync(Func<T, CancellationToken, ValueTask<bool>> predicate, string message, string? hint = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(message);
        return AddRefinement(new SchemaRefinement<T>(Materialize, null, predicate, message, string.Empty, hint));
    }

    private protected TSelf AddRefinement(SchemaRefinement refinement) => With(Node with
    {
        Behavior = Node.Behavior with { Refinements = Node.Behavior.Refinements.Add(refinement) },
    });

    private protected abstract TSelf With(SchemaNode node);
}
