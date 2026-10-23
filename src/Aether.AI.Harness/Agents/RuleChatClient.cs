using Aether.AI.Brains.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aether.AI.Harness.Agents;

/// <summary>Compatibility name for <see cref="BrainChatClient"/>.</summary>
[Obsolete("Use BrainChatClient.")]
public sealed class RuleChatClient : BrainChatClient
{
    public RuleChatClient(IBrain brain, ILogger<BrainChatClient>? logger = null)
        : base(brain, logger)
    {
    }
}
