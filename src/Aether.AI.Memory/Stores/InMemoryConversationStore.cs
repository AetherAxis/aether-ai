using System.Collections.Concurrent;
using Aether.AI.Core.Conversations;
using Aether.AI.Core.Conversations.Runs;

namespace Aether.AI.Memory.Stores;

/// <summary>
/// In-process implementation of <see cref="IConversationStore"/>. Stores conversation metadata
/// plus run/event lists in memory. Used by tests and as a fallback when SQLite is not wired up.
/// </summary>
public sealed class InMemoryConversationStore : IConversationStore
{
    private readonly ConcurrentDictionary<Guid, Conversation> _conversations = new();
    private readonly ConcurrentDictionary<Guid, List<AgentRun>> _runsByConversation = new();
    private readonly ConcurrentDictionary<Guid, List<AgentEvent>> _eventsByRun = new();

    public Task SaveConversationAsync(Conversation conversation, CancellationToken cancellationToken = default)
    {
        if (conversation is null) throw new ArgumentNullException(nameof(conversation));
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        _conversations[conversation.ConversationId] = conversation;
        return Task.CompletedTask;
    }

    public Task<Conversation?> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        _conversations.TryGetValue(conversationId, out var conversation);
        return Task.FromResult(conversation);
    }

    public Task<IReadOnlyList<ConversationSummary>> GetSummariesAsync(string userId, CancellationToken cancellationToken = default)
    {
        // NULL 容忍，镜像 SqliteConversationStore 的 `@uid IS NULL OR user_id IS NULL OR user_id=@uid`：
        // 桌面单用户的 Conversation.UserId 是 null（SQLite 落 NULL），查询传 ""——严格 == 会漏掉全部会话
        // （store 兜底切换时历史列表恒空）。GetRunsInRangeAsync 同语义。
        var results = _conversations.Values
            .Where(c => userId is null || c.UserId is null || c.UserId == userId)
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new ConversationSummary(
                c.ConversationId, c.Title, c.AgentId, c.AgentName, c.UpdatedAt,
                c.LastMessagePreview ?? string.Empty))
            .ToList();
        return Task.FromResult<IReadOnlyList<ConversationSummary>>(results);
    }

    public Task<IReadOnlyList<Conversation>> FindByUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        var results = _conversations.Values
            .Where(c => userId is null || c.UserId is null || c.UserId == userId)
            .OrderByDescending(c => c.UpdatedAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<Conversation>>(results);
    }

    public Task<bool> DeleteAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var removed = _conversations.TryRemove(conversationId, out _);
        if (_runsByConversation.TryRemove(conversationId, out var runs))
        {
            foreach (var run in runs)
                _eventsByRun.TryRemove(run.Id, out _);
        }
        return Task.FromResult(removed);
    }

    public Task AppendRunAsync(AgentRun run, IReadOnlyList<AgentEvent> events, CancellationToken cancellationToken = default)
    {
        if (run is null) throw new ArgumentNullException(nameof(run));
        if (events is null) throw new ArgumentNullException(nameof(events));

        var runs = _runsByConversation.GetOrAdd(run.ConversationId, _ => new());
        lock (runs)
        {
            runs.Add(run);
        }
        _eventsByRun[run.Id] = events.ToList();

        // Denormalize preview/updatedAt on the conversation if present.
        if (_conversations.TryGetValue(run.ConversationId, out var conv))
        {
            conv.LastMessagePreview = ComputePreview(run, events);
            conv.LastMessageAt = run.FinishedAt ?? run.StartedAt;
            conv.UpdatedAt = DateTimeOffset.UtcNow;
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AgentRun>> GetRunsAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        _runsByConversation.TryGetValue(conversationId, out var runs);
        IReadOnlyList<AgentRun> result = runs is null
            ? Array.Empty<AgentRun>()
            : runs.OrderBy(r => r.Seq).ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<AgentEvent>> GetEventsAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        _eventsByRun.TryGetValue(runId, out var events);
        IReadOnlyList<AgentEvent> result = events is null
            ? Array.Empty<AgentEvent>()
            : events.OrderBy(e => e.Seq).ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<RunSummary>> GetRunsInRangeAsync(string userId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        var list = new List<RunSummary>();
        foreach (var (convId, runs) in _runsByConversation)
        {
            // join conversation 取 user_id 过滤 + title。NULL 容忍同 GetSummariesAsync
            //（与 Sqlite 的 `@uid IS NULL OR c.user_id IS NULL OR c.user_id=@uid` 对齐——
            // 旧版 `(conv.UserId ?? "") != uid` 在 conv.UserId=null、uid="" 时才匹配，语义偏窄）。
            if (!_conversations.TryGetValue(convId, out var conv)
                || !(userId is null || conv.UserId is null || conv.UserId == userId)) continue;
            foreach (var run in runs)
            {
                if (run.StartedAt < from || run.StartedAt >= to) continue;
                list.Add(new RunSummary(
                    run.Id, run.ConversationId, conv.Title,
                    run.AgentId, run.AgentName, run.Status,
                    run.StartedAt, run.FinishedAt, run.UserMessage));
            }
        }
        list.Sort((a, b) => a.StartedAt.CompareTo(b.StartedAt));
        return Task.FromResult<IReadOnlyList<RunSummary>>(list);
    }

    private static string ComputePreview(AgentRun run, IReadOnlyList<AgentEvent> events)
    {
        // Prefer the last answer text; fall back to the user message.
        var lastText = events.LastOrDefault(e => e.EventType == AgentEventType.TextChunk);
        if (lastText is not null)
        {
            var text = AgentEventPayload.Deserialize<AgentEventPayload.TextChunk>(lastText.Payload)?.Text;
            if (!string.IsNullOrWhiteSpace(text))
            {
                const int Max = 80;
                text = text.Replace('\n', ' ').Trim();
                return text.Length <= Max ? text : text[..Max] + "…";
            }
        }
        const int MaxU = 80;
        var um = run.UserMessage.Replace('\n', ' ').Trim();
        return um.Length <= MaxU ? um : um[..MaxU] + "…";
    }
}
