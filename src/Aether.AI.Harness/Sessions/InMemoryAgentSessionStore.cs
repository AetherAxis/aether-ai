using System.Collections.Concurrent;
using System.Text.Json;

namespace Aether.AI.Harness.Sessions;

/// <summary>
/// 进程内会话存储（默认的「可插拔」实现，也是测试夹具）。只在进程生命周期内有效——
/// 需要跨重启恢复时换成 SQLite/文件/远端实现，接口不变。
/// </summary>
public sealed class InMemoryAgentSessionStore : IAgentSessionStore
{
    private readonly ConcurrentDictionary<string, JsonElement> _sessions = new(StringComparer.Ordinal);

    /// <summary>当前保存的会话条数（诊断/测试用）。</summary>
    public int Count => _sessions.Count;

    public ValueTask<JsonElement?> GetAsync(string key, CancellationToken cancellationToken = default)
        => new(_sessions.TryGetValue(key, out var session) ? session : null);

    public ValueTask SaveAsync(string key, JsonElement session, CancellationToken cancellationToken = default)
    {
        // Clone：JsonElement 可能由已释放的 JsonDocument 持有，直接存会在后续读取时抛：
        _sessions[key] = session.Clone();
        return default;
    }

    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        _sessions.TryRemove(key, out _);
        return default;
    }
}
