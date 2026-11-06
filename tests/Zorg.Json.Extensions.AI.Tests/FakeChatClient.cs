using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace Zorg.Json.Extensions.AI.Tests;

/// <summary>A chat client that answers from a delegate. It never touches the network.</summary>
internal sealed class FakeChatClient(Func<IReadOnlyList<ChatMessage>, ChatOptions?, CancellationToken, ChatResponse> respond) : IChatClient
{
    public FakeChatClient(string text)
        : this((_, _, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant, text)))
    {
    }

    public List<ChatOptions?> Options { get; } = [];

    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        List<ChatMessage> list = messages.ToList();
        Requests.Add(list);
        Options.Add(options);
        return Task.FromResult(respond(list, options, cancellationToken));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);

        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
