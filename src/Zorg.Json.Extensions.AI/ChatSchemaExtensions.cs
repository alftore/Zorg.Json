using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Zorg.Json.Emission;
using Zorg.Json.Llm;

namespace Zorg.Json.Extensions.AI;

/// <summary>Sends a Zorg.Json schema to a chat client as the response format, and parses the answer.</summary>
public static class ChatSchemaExtensions
{
    /// <summary>
    /// Makes a response format that asks the model for JSON that fits the schema. Zod equivalent: <c>Output.object({ schema })</c>
    /// in the Vercel AI SDK.
    /// </summary>
    /// <typeparam name="T">The type of the parsed value.</typeparam>
    /// <param name="schema">The schema.</param>
    /// <param name="profile">The profile to emit with, such as <see cref="LlmProfiles.OpenAIStrict"/>. The default is plain Draft 2020-12.</param>
    /// <param name="schemaName">A name for the schema. Some providers require one, and the OpenAI adapter supplies its own when this is null.</param>
    /// <param name="schemaDescription">What the schema is for.</param>
    /// <returns>The format, to set as <see cref="ChatOptions.ResponseFormat"/>. Parse the answer with the schema and the same profile, since the profile may have wrapped or loosened it.</returns>
    /// <exception cref="InvalidOperationException">Emitting stopped because the schema is too large.</exception>
    /// <example>
    /// <code>
    /// ChatOptions options = new() { ResponseFormat = Answer.ToChatResponseFormat(LlmProfiles.OpenAIStrict, "answer") };
    /// </code>
    /// </example>
    public static ChatResponseFormat ToChatResponseFormat<T>(
        this Schema<T> schema,
        JsonSchemaProfile? profile = null,
        string? schemaName = null,
        string? schemaDescription = null) =>
        schema.ToChatResponseFormat(profile, out _, schemaName, schemaDescription);

    /// <summary>
    /// Makes a response format, and returns the emit result to parse the answer with
    /// <see cref="LlmSchemaExtensions.ParseModelOutput{T}(Schema{T}, EmitResult, string)"/>.
    /// </summary>
    /// <typeparam name="T">The type of the parsed value.</typeparam>
    /// <param name="schema">The schema.</param>
    /// <param name="profile">The profile to emit with. The default is plain Draft 2020-12.</param>
    /// <param name="emitted">The emit result, with the diagnostics for what the profile couldn't express.</param>
    /// <param name="schemaName">A name for the schema.</param>
    /// <param name="schemaDescription">What the schema is for.</param>
    /// <returns>The format.</returns>
    /// <exception cref="InvalidOperationException">Emitting stopped because the schema is too large.</exception>
    /// <example>
    /// <code>
    /// ChatResponseFormat format = Answer.ToChatResponseFormat(LlmProfiles.AnthropicStrict, out EmitResult emitted);
    /// foreach (EmitDiagnostic diagnostic in emitted.Diagnostics) { Console.WriteLine(diagnostic.Message); }
    /// </code>
    /// </example>
    public static ChatResponseFormat ToChatResponseFormat<T>(
        this Schema<T> schema,
        JsonSchemaProfile? profile,
        out EmitResult emitted,
        string? schemaName = null,
        string? schemaDescription = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        emitted = schema.Emit(profile ?? JsonSchemaProfile.Draft202012);
        return ChatResponseFormat.ForJsonSchema(ErrorReport.ParseSchema(emitted.Schema.ToJsonString()), schemaName, schemaDescription);
    }

    /// <summary>
    /// Asks the model for JSON that fits the schema and parses the answer. Zod equivalent: <c>generateObject({ schema })</c>
    /// in the Vercel AI SDK.
    /// </summary>
    /// <remarks>
    /// The text of the last message that has any is parsed with <c>ParseModelOutputAsync</c>, against the full schema.
    /// Malformed, truncated or empty output is a failed result, not an exception. A client that throws, and a
    /// cancelled <paramref name="cancellationToken"/>, still throw. The answer's usage and finish reason are not returned.
    /// <paramref name="options"/> is copied, not changed.
    /// </remarks>
    /// <typeparam name="T">The type of the parsed value.</typeparam>
    /// <param name="chatClient">The client.</param>
    /// <param name="schema">The schema.</param>
    /// <param name="messages">The conversation.</param>
    /// <param name="options">Options for the request. Its <see cref="ChatOptions.ResponseFormat"/> is replaced.</param>
    /// <param name="profile">The profile to emit with, such as <see cref="LlmProfiles.OpenAIStrict"/>. The default is plain Draft 2020-12.</param>
    /// <param name="schemaName">A name for the schema.</param>
    /// <param name="schemaDescription">What the schema is for.</param>
    /// <param name="cancellationToken">Cancels the request and any async refinements.</param>
    /// <returns>The value, or the issues.</returns>
    /// <example>
    /// <code>
    /// ParseResult&lt;JsonElement&gt; answer = await chatClient.GetResponseAsync(Answer, [new ChatMessage(ChatRole.User, "Rate this.")]);
    /// if (answer.IsSuccess) { Console.WriteLine(answer.Value); }
    /// </code>
    /// </example>
    public static async Task<ParseResult<T>> GetResponseAsync<T>(
        this IChatClient chatClient,
        Schema<T> schema,
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        JsonSchemaProfile? profile = null,
        string? schemaName = null,
        string? schemaDescription = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(messages);

        ChatResponseFormat format = schema.ToChatResponseFormat(profile, out EmitResult emitted, schemaName, schemaDescription);
        ChatOptions request = options?.Clone() ?? new ChatOptions();
        request.ResponseFormat = format;

        ChatResponse? response = await chatClient.GetResponseAsync(messages, request, cancellationToken).ConfigureAwait(false);

        return await schema.ParseModelOutputAsync(emitted, AnswerText(response), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Asks the model for JSON that fits the schema, with one user message. See the other overload.</summary>
    /// <typeparam name="T">The type of the parsed value.</typeparam>
    /// <param name="chatClient">The client.</param>
    /// <param name="schema">The schema.</param>
    /// <param name="chatMessage">The user message.</param>
    /// <param name="options">Options for the request. Its <see cref="ChatOptions.ResponseFormat"/> is replaced.</param>
    /// <param name="profile">The profile to emit with. The default is plain Draft 2020-12.</param>
    /// <param name="schemaName">A name for the schema.</param>
    /// <param name="schemaDescription">What the schema is for.</param>
    /// <param name="cancellationToken">Cancels the request and any async refinements.</param>
    /// <returns>The value, or the issues.</returns>
    /// <example>
    /// <code>
    /// ParseResult&lt;JsonElement&gt; answer = await chatClient.GetResponseAsync(Answer, "Rate this.", profile: LlmProfiles.OpenAIStrict);
    /// </code>
    /// </example>
    public static Task<ParseResult<T>> GetResponseAsync<T>(
        this IChatClient chatClient,
        Schema<T> schema,
        string chatMessage,
        ChatOptions? options = null,
        JsonSchemaProfile? profile = null,
        string? schemaName = null,
        string? schemaDescription = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatMessage);
        return chatClient.GetResponseAsync(schema, [new ChatMessage(ChatRole.User, chatMessage)], options, profile, schemaName, schemaDescription, cancellationToken);
    }

    // The last assistant message with text is the answer. Earlier ones can be a remark before a tool call.
    private static string AnswerText(ChatResponse? response)
    {
        if (response is null)
        {
            return string.Empty;
        }

        for (int index = response.Messages.Count - 1; index >= 0; index--)
        {
            // Only the model's own words. A tool result or a user message in the list is not its answer.
            if (response.Messages[index].Role != ChatRole.Assistant)
            {
                continue;
            }

            string text = response.Messages[index].Text;

            if (text.Length > 0)
            {
                return text;
            }
        }

        return string.Empty;
    }
}
