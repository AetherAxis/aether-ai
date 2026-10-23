using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// <see cref="ChatHistoryProvider"/> that neither retrieves nor stores any
/// conversation history — each agent turn runs independently with no prior
/// context. Implements the <c>Off</c> memory policy.
/// </summary>
public sealed class NoMemoryChatHistoryProvider : ChatHistoryProvider
{
    protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        ChatHistoryProvider.InvokingContext context,
        CancellationToken cancellationToken = default)
        => new(Array.Empty<ChatMessage>());

    protected override ValueTask StoreChatHistoryAsync(
        ChatHistoryProvider.InvokedContext context,
        CancellationToken cancellationToken = default)
        => default;
}
