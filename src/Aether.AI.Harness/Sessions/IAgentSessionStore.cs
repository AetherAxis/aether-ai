using System.Text.Json;

namespace Aether.AI.Harness.Sessions;

/// <summary>
/// 框架会话（<see cref="Microsoft.Agents.AI.AgentSession"/>）的持久化端口。
/// <para>运行时默认只在内存里持有会话对象；配置了本存储后，会话在创建与每轮结束后按
/// <see cref="JsonElement"/> 落盘，重启进程可续用同一会话。序列化/反序列化由
/// <see cref="Microsoft.Agents.AI.AIAgent.SerializeSessionAsync"/> /
/// <see cref="Microsoft.Agents.AI.AIAgent.DeserializeSessionAsync"/> 负责，存储只搬字节。</para>
/// <para>键由 <see cref="AgentSessionKeys.For"/> 生成：agent 身份 + 会话 id（+ 可选分区）。
/// 同一会话换了 agent 定义就是另一条记录，不会把 A 的会话状态灌进 B。</para>
/// </summary>
public interface IAgentSessionStore
{
    /// <summary>读取会话状态；不存在返回 <c>null</c>。</summary>
    ValueTask<JsonElement?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>写入（覆盖）会话状态。</summary>
    ValueTask SaveAsync(string key, JsonElement session, CancellationToken cancellationToken = default);

    /// <summary>删除会话状态。会话被删除或重建时调用，避免下次启动把已删会话「复活」。</summary>
    ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>会话存储键的单一格式来源——运行时与 MAF 适配器必须算出同一个键，否则读到两条记录。</summary>
public static class AgentSessionKeys
{
    /// <summary>拼键：<c>agentId|sessionId[|k=v;k=v]</c>（分区按 key 序数排序，保证同一逻辑键稳定）。</summary>
    public static string For(
        string agentId, string sessionId, IReadOnlyDictionary<string, string>? partitions = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);

        if (partitions is not { Count: > 0 })
            return $"{agentId}|{sessionId}";

        var parts = string.Join(
            ";",
            partitions.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));
        return $"{agentId}|{sessionId}|{parts}";
    }
}
