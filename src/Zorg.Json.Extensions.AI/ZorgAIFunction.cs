using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Zorg.Json.Emission;
using Zorg.Json.Llm;

namespace Zorg.Json.Extensions.AI;

/// <summary>
/// A tool for a chat client whose arguments are described and checked by a Zorg.Json schema. The model sees the
/// schema as <see cref="JsonSchema"/>. When it calls the function, the arguments are validated against the full schema
/// first, and a refusal goes back to the model as a structured error, so it can correct the call, instead of an exception.
/// Zod equivalent: the <c>inputSchema</c> of a Vercel AI SDK <c>tool()</c>.
/// </summary>
/// <remarks>
/// An exception thrown by your function, or by a <c>Refine</c> check on the schema, is not caught here. It reaches the
/// caller as it would from any <see cref="AIFunction"/>, and <c>FunctionInvokingChatClient</c> decides what to do with it.
/// </remarks>
/// <example>
/// <code>
/// static readonly ObjectSchema Arguments = Z.Object()
///     .Property("city", Z.String().MinLength(1))
///     .Property("days", Z.Integer().Minimum(1).Maximum(14));
///
/// AIFunction forecast = ZorgAIFunction.Create("get_forecast", "Gets a weather forecast.", Arguments,
///     arguments => $"Sunny in {arguments.GetProperty("city").GetString()}.");
/// ChatOptions options = new() { Tools = [forecast] };
/// </code>
/// </example>
public sealed class ZorgAIFunction : AIFunction
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { AllowDuplicateProperties = false };

    private readonly Func<JsonElement, CancellationToken, ValueTask<object?>> _invoke;

    private ZorgAIFunction(string name, string description, JsonElement jsonSchema, Func<JsonElement, CancellationToken, ValueTask<object?>> invoke)
    {
        Name = name;
        Description = description;
        JsonSchema = jsonSchema;
        _invoke = invoke;
    }

    /// <summary>The name the model calls the function by.</summary>
    public override string Name { get; }

    /// <summary>What the function does, for the model.</summary>
    public override string Description { get; }

    /// <summary>The JSON Schema of the arguments, emitted from the schema with the profile given to <c>Create</c>.</summary>
    public override JsonElement JsonSchema { get; }

    /// <summary>
    /// Creates a function that runs asynchronously. Zod equivalent: <c>tool({ inputSchema, execute })</c> in the Vercel AI SDK.
    /// </summary>
    /// <typeparam name="T">The type of the parsed arguments.</typeparam>
    /// <param name="name">The function's name. It must not be empty.</param>
    /// <param name="description">What the function does. It can be empty.</param>
    /// <param name="arguments">The schema of the arguments. Its root must be an object, because providers take no other root for a tool.</param>
    /// <param name="function">Called with the validated arguments. Whatever it returns goes back to the model.</param>
    /// <param name="profile">The profile to emit <see cref="JsonSchema"/> with, such as <see cref="LlmProfiles.OpenAIStrict"/>. The default is plain Draft 2020-12.</param>
    /// <returns>The function.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty, or the root of <paramref name="arguments"/> isn't an object.</exception>
    /// <example>
    /// <code>
    /// AIFunction lookup = ZorgAIFunction.Create("lookup", "Finds an order.", Arguments,
    ///     async (arguments, cancellationToken) => await orders.FindAsync(arguments, cancellationToken));
    /// </code>
    /// </example>
    public static ZorgAIFunction Create<T>(
        string name,
        string description,
        Schema<T> arguments,
        Func<T, CancellationToken, ValueTask<object?>> function,
        JsonSchemaProfile? profile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(function);

        if (!Rewriters.HasObjectRoot(arguments.ToNode()))
        {
            throw new ArgumentException("The root of a function's arguments must be an object. Wrap other schemas in an object property.", nameof(arguments));
        }

        EmitResult emitted = arguments.Emit(profile ?? JsonSchemaProfile.Draft202012);

        async ValueTask<object?> Invoke(JsonElement json, CancellationToken cancellationToken)
        {
            ParseResult<T> parsed = await arguments.ParseModelOutputAsync(emitted, json, cancellationToken).ConfigureAwait(false);
            return parsed.IsSuccess
                ? await function(parsed.Value, cancellationToken).ConfigureAwait(false)
                : ErrorReport.Create("The arguments don't match the schema. Fix them and call the function again.", parsed.Issues);
        }

        return new ZorgAIFunction(name, description, JsonElement.Parse(emitted.Schema.ToJsonString()), Invoke);
    }

    /// <summary>Creates a function that runs synchronously. See the other overload.</summary>
    /// <typeparam name="T">The type of the parsed arguments.</typeparam>
    /// <param name="name">The function's name. It must not be empty.</param>
    /// <param name="description">What the function does. It can be empty.</param>
    /// <param name="arguments">The schema of the arguments. Its root must be an object.</param>
    /// <param name="function">Called with the validated arguments. Whatever it returns goes back to the model.</param>
    /// <param name="profile">The profile to emit <see cref="JsonSchema"/> with. The default is plain Draft 2020-12.</param>
    /// <returns>The function.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty, or the root of <paramref name="arguments"/> isn't an object.</exception>
    /// <example>
    /// <code>
    /// AIFunction add = ZorgAIFunction.Create("add", "Adds two numbers.", Arguments,
    ///     arguments => arguments.GetProperty("a").GetInt32() + arguments.GetProperty("b").GetInt32());
    /// </code>
    /// </example>
    public static ZorgAIFunction Create<T>(string name, string description, Schema<T> arguments, Func<T, object?> function, JsonSchemaProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(function);
        return Create(name, description, arguments, (T value, CancellationToken _) => ValueTask.FromResult(function(value)), profile);
    }

    /// <inheritdoc/>
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryReadArguments(arguments, JsonSerializerOptions, out JsonElement json, out string? name))
        {
            return ErrorReport.Create(
                name is null ? "The arguments can't be read as JSON." : $"The argument '{name}' can't be read as JSON.",
                []);
        }

        return await _invoke(json, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryReadArguments(AIFunctionArguments arguments, JsonSerializerOptions options, out JsonElement json, out string? failedName)
    {
        ArrayBufferWriter<byte> buffer = new();
        string? current = null;

        try
        {
            using (Utf8JsonWriter writer = new(buffer))
            {
                writer.WriteStartObject();

                foreach (KeyValuePair<string, object?> pair in arguments)
                {
                    current = pair.Key;
                    writer.WritePropertyName(pair.Key);
                    WriteValue(writer, pair.Value, options);
                }

                writer.WriteEndObject();
            }

            json = JsonElement.Parse(buffer.WrittenSpan, DocumentOptions);
            failedName = null;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or JsonException)
        {
            // A lone surrogate, a type with no JSON contract, or nesting too deep. The name is only reported when it is text we can print.
            json = default;
            failedName = current is null ? null : ErrorReport.Clean(current);
            return false;
        }
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonElement { ValueKind: JsonValueKind.Undefined }:
                writer.WriteNullValue();
                break;
            case JsonElement element:
                // Written as the text it came in, so an escaped lone surrogate reaches the schema, which reports it.
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;
            case JsonNode node:
                node.WriteTo(writer, options);
                break;
            default:
                JsonTypeInfo typeInfo = options.GetTypeInfo(value.GetType());
                JsonSerializer.Serialize(writer, value, typeInfo);
                break;
        }
    }
}
