using Microsoft.Agents.AI;

namespace Aether.AI.Harness.Sessions;

/// <summary>
/// 把 <see cref="IAgentSessionStore"/> 适配成 MAF 的 <see cref="Microsoft.Agents.AI.AgentSessionStore"/>，
/// 使 MAF 生态（Hosting / DevUI / 远端编排）能读写 Aether 的会话，反之亦然——两个方向共用同一份键格式
/// （<see cref="AgentSessionKeys"/>）。
/// <para>MAF 的 store 契约以 <see cref="AIAgent"/> 为参数（它负责序列化），Aether 的以字节为准；
/// 本适配器就是这两层之间的翻译。</para>
/// </summary>
public sealed class MafAgentSessionStore : Microsoft.Agents.AI.AgentSessionStore
{
    private readonly IAgentSessionStore _inner;

    public MafAgentSessionStore(IAgentSessionStore inner)
        => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public override async ValueTask SaveSessionAsync(
        AIAgent agent, AgentSessionStoreKey key, AgentSession session, CancellationToken cancellationToken = default)
    {
        var json = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken).ConfigureAwait(false);
        await _inner.SaveAsync(ToStoreKey(agent, key), json, cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask<AgentSession> GetSessionAsync(
        AIAgent agent, AgentSessionStoreKey key, CancellationToken cancellationToken = default)
    {
        var json = await _inner.GetAsync(ToStoreKey(agent, key), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No persisted session for key '{ToStoreKey(agent, key)}'.");
        return await agent.DeserializeSessionAsync(json, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask<AgentSession> GetOrCreateSessionAsync(
        AIAgent agent, AgentSessionStoreKey key, CancellationToken cancellationToken = default)
    {
        var storeKey = ToStoreKey(agent, key);
        if (await _inner.GetAsync(storeKey, cancellationToken).ConfigureAwait(false) is { } persisted)
            return await agent.DeserializeSessionAsync(persisted, cancellationToken: cancellationToken).ConfigureAwait(false);

        var created = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        await SaveSessionAsync(agent, key, created, cancellationToken).ConfigureAwait(false);
        return created;
    }

    private static string ToStoreKey(AIAgent agent, AgentSessionStoreKey key)
        => AgentSessionKeys.For(agent.Id, key.SessionId, key.Partitions);
}
