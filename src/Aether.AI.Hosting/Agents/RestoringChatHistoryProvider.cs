using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Aether.AI.Core.Conversations.Runs;
using Aether.AI.Harness.Agents;
using Aether.AI.Memory;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Aether.AI.Hosting.Agents;

/// <summary>
/// 会话真恢复：继承 <see cref="ChatHistoryProvider"/>，首次从 DB 加载历史（跨重启恢复），
/// 后续轮次靠内存累积（和 <see cref="InMemoryChatHistoryProvider"/> 行为一致）。
/// <para>关键：<see cref="StoreChatHistoryAsync"/> 不再 no-op——正确保存 per-session 消息列表，
/// TodoProvider / AgentModeProvider 的状态管理依赖此机制。</para>
/// </summary>
public sealed class RestoringChatHistoryProvider : ChatHistoryProvider
{
    private readonly IConversationStore? _store;
    private readonly SessionConversationMap _sessionMap;
    private readonly IChatReducer? _reducer;
    private readonly ILoggerFactory? _loggerFactory;
    // per-session 内存缓存（和 InMemoryChatHistoryProvider 一样）。用 ConditionalWeakTable 弱键持有——
    // session 一旦被运行时清出（EvictSession/EvictConversation）即失去强引用 → 本表条目 + 历史列表随 GC
    // 自动回收，无需 provider↔runtime 的清理布线（镜像 SessionConversationMap 的弱键模式）。
    private readonly ConditionalWeakTable<AgentSession, List<ChatMessage>> _sessionMessages = new();
    // 标记「已尝试从 DB 恢复」，避免每次 Provide 都查库。弱键：随 session 回收自动清。
    private readonly ConditionalWeakTable<AgentSession, object> _restoredSessions = new();

    public RestoringChatHistoryProvider(IConversationStore? store, SessionConversationMap sessionMap,
        IChatReducer? reducer = null, ILoggerFactory? loggerFactory = null)
    {
        _store = store;
        _sessionMap = sessionMap ?? throw new ArgumentNullException(nameof(sessionMap));
        _reducer = reducer;
        _loggerFactory = loggerFactory;
    }

    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(InvokingContext context, CancellationToken cancellationToken)
    {
        // 首次：从 DB 恢复历史，存入内存缓存
        if (_store is not null && _sessionMap.TryGet(context.Session, out var conversationId) && !_restoredSessions.TryGetValue(context.Session, out _))
        {
            try
            {
                var conv = await _store.GetConversationAsync(conversationId, cancellationToken).ConfigureAwait(false);
                if (conv is not null)
                {
                    // 从 typed 事件流重建 LLM 历史（取代旧的 flat-Message Convert+Sanitize saga）。
                    // 结构不变量由构造保证：CallId 在事件写入时即配对、call↔result 相邻、Interrupted 尾部孤儿 call 被丢。
                    var runs = await _store.GetRunsAsync(conversationId, cancellationToken).ConfigureAwait(false);
                    var eventsByRun = new Dictionary<Guid, IReadOnlyList<AgentEvent>>();
                    foreach (var run in runs)
                    {
                        eventsByRun[run.Id] = await _store.GetEventsAsync(run.Id, cancellationToken).ConfigureAwait(false);
                    }
                    var messages = EventHistoryRebuilder.RebuildLlmHistory(runs, eventsByRun);
                    _sessionMessages.AddOrUpdate(context.Session, messages.ToList());
                }
                _restoredSessions.AddOrUpdate(context.Session, new object());
            }
            catch (Exception ex)
            {
                _loggerFactory?.CreateLogger<RestoringChatHistoryProvider>()?.LogWarning(ex, "恢复会话历史失败，使用空历史。");
                _restoredSessions.AddOrUpdate(context.Session, new object());
            }
        }

        // 从内存返回历史
        var list = _sessionMessages.GetValue(context.Session, _ => new List<ChatMessage>());

        // 应用 reducer（SlidingWindow 截断），再净化。用 SanitizeProviderHistory（非 Sanitize）——
        // 活路径 FICC 循环里最近一轮的 tool_call 在 Provide 时尚无 result（下一轮 Store 才落地），
        // Sanitize 会把它当孤儿丢弃 → 下一轮请求变成「有 result 无 call」→ 模型重调工具（重复执行两遍）。
        // SanitizeProviderHistory 只丢孤儿 tool_result，保留进行中的 tool_call。
        IEnumerable<ChatMessage> history = list;
        if (_reducer is not null)
            history = await _reducer.ReduceAsync(list, cancellationToken).ConfigureAwait(false);
        return TranscriptHistoryConverter.SanitizeProviderHistory(history);
    }

    /// <summary>
    /// 保存 per-session 消息列表到内存。过滤掉 GLM/Anthropic 不认识的内容类型
    /// （ToolApprovalRequest/Response、TextReasoning、Error、Usage 等），只保留
    /// TextContent / FunctionCallContent / FunctionResultContent + 跳过空消息。
    /// </summary>
    protected override ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken)
    {
        if (context.Session is null) return default;

        var list = _sessionMessages.GetValue(context.Session, _ => new List<ChatMessage>());
        foreach (var msg in context.RequestMessages ?? Enumerable.Empty<ChatMessage>())
        {
            var filtered = FilterMessage(msg);
            if (filtered is not null) list.Add(filtered);
        }
        foreach (var msg in context.ResponseMessages ?? Enumerable.Empty<ChatMessage>())
        {
            var filtered = FilterMessage(msg);
            if (filtered is not null) list.Add(filtered);
        }
        return default;
    }

    /// <summary>
    /// 过滤消息：只保留 GLM 能理解的 content（TextContent / FunctionCallContent / FunctionResultContent）。
    /// 跳过 ToolApprovalRequest/Response、TextReasoning、Error、Usage 等 MAF 内部类型。
    /// 空消息（过滤后无 content）也跳过——空 assistant 会触发 400。
    /// </summary>
    private static ChatMessage? FilterMessage(ChatMessage msg)
    {
        var validContents = msg.Contents.Where(c => c is TextContent or FunctionCallContent or FunctionResultContent).ToList();
        if (validContents.Count == 0) return null; // 过滤后为空 → 跳过

        // 如果没过滤掉任何 content，直接返回原消息（避免不必要拷贝）
        if (validContents.Count == msg.Contents.Count) return msg;

        return new ChatMessage { Role = msg.Role, Contents = validContents };
    }
}
