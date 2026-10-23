using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// session→conversationId 映射（ConditionalWeakTable，session 回收自动清理）。
/// <see cref="HarnessRuntime"/> 建 session 时 Register；<c>RestoringChatHistoryProvider</c> TryGet 以加载对应 transcript。
/// </summary>
public sealed class SessionConversationMap
{
    private sealed class GuidBox { public Guid Value; }
    private readonly ConditionalWeakTable<AgentSession, GuidBox> _map = new();

    public void Register(AgentSession session, Guid conversationId)
        => _map.AddOrUpdate(session, new GuidBox { Value = conversationId });

    public bool TryGet(AgentSession session, out Guid conversationId)
    {
        if (_map.TryGetValue(session, out var box)) { conversationId = box.Value; return true; }
        conversationId = default;
        return false;
    }
}
