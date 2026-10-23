using System.Text.Json;
using Aether.AI.Core.Memory;
using Aether.AI.Harness;
using Aether.AI.Harness.Agents;
using Aether.AI.Memory;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Aether.AI.Hosting.Agents;

/// <summary>
/// 把 <see cref="IMemoryStore"/>（长期记忆）接入 agent 的 <see cref="AIContextProvider"/>：
/// <list type="bullet">
/// <item><see cref="ProvideAIContextAsync"/>：每轮 run 前召回当前用户的长期记忆，作为
///   <see cref="AIContext.Instructions"/> 注入（跨会话保留的用户偏好/事实/任务历史）。</item>
/// <item><see cref="StoreAIContextAsync"/>：每轮 run 后把本轮 Q&amp;A 沉淀为一条
///   <see cref="MemoryType.TaskHistory"/> 记忆项，供后续召回。</item>
/// </list>
/// 区别于会话内聊天历史窗口（<c>SlidingWindowChatReducer</c> 等）——这是跨会话的提炼记忆。
/// <para>userId 运行时由 <see cref="_userIdAccessor"/> 解析，避免缓存 agent 在登录/登出后失效。</para>
/// </summary>
public sealed class MemoryContextProvider : AIContextProvider
{
    private readonly IMemoryStore _store;
    private readonly Func<string?> _userIdAccessor;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly IChatClientFactory? _chatClientFactory;
    private int _storeCount;

    public MemoryContextProvider(IMemoryStore store, Func<string?> userIdAccessor, ILoggerFactory? loggerFactory = null,
        IChatClientFactory? chatClientFactory = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _userIdAccessor = userIdAccessor ?? throw new ArgumentNullException(nameof(userIdAccessor));
        _loggerFactory = loggerFactory;
        _chatClientFactory = chatClientFactory;
    }

    /// <summary>召回长期记忆并注入为 system 指令。无记忆时返回空 <see cref="AIContext"/>（不改变行为）。</summary>
    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken)
    {
        try
        {
            var block = await RecallInstructionsAsync(_userIdAccessor(), cancellationToken).ConfigureAwait(false);
            return string.IsNullOrEmpty(block) ? new AIContext() : new AIContext { Instructions = block };
        }
        catch (Exception ex)
        {
            _loggerFactory?.CreateLogger<MemoryContextProvider>()?.LogWarning(ex, "召回长期记忆失败，跳过注入。");
            return new AIContext(); // 记忆失败不阻断主流程
        }
    }

    /// <summary>把本轮 Q&amp;A 沉淀为一条 TaskHistory 记忆项。失败静默（不阻断 run 完成）。</summary>
    protected override async ValueTask StoreAIContextAsync(InvokedContext context, CancellationToken cancellationToken)
    {
        if (context.InvokeException is not null) return; // 失败的 run 不沉淀

        try
        {
            var userId = _userIdAccessor();
            var (question, answer) = ExtractTurn(context.RequestMessages, context.ResponseMessages);
            var item = BuildEpisodicMemory(userId, question, answer);
            if (item is null) return;
            await _store.SaveAsync(item, cancellationToken).ConfigureAwait(false);

            // 每 5 轮提炼一次：把近期 episodic 压成 Knowledge/UserPreference（少而精）。
            if (_chatClientFactory is not null && Interlocked.Increment(ref _storeCount) % 5 == 0)
                _ = Task.Run(() => DistillAsync(userId, CancellationToken.None));
        }
        catch (Exception ex)
        {
            _loggerFactory?.CreateLogger<MemoryContextProvider>()?.LogWarning(ex, "沉淀长期记忆失败。");
        }
    }

    /// <summary>
    /// LLM 提炼：读近期 episodic + 已有 Knowledge/Preference，让 LLM 提取持久事实/偏好，
    /// 存为 Knowledge/UserPreference 记忆项。fire-and-forget，失败静默。
    /// </summary>
    private async Task DistillAsync(string? userId, CancellationToken cancellationToken)
    {
        try
        {
            var episodic = await _store.FindByTypeAsync(userId, MemoryType.TaskHistory, cancellationToken).ConfigureAwait(false);
            if (episodic.Count < 3) return; // 太少不提炼

            var existing = (await _store.FindByTypeAsync(userId, MemoryType.Knowledge, cancellationToken).ConfigureAwait(false))
                .Concat(await _store.FindByTypeAsync(userId, MemoryType.UserPreference, cancellationToken).ConfigureAwait(false)).ToList();

            var recentEpisodic = string.Join("\n---\n", episodic.Take(10).Select(e => e.Content));
            var existingText = existing.Count == 0 ? "（无）" : string.Join("\n", existing.Select(e => $"[{e.Type}] {e.Content}"));

            var prompt = $@"你是记忆提炼器。从以下用户与 AI 的对话片段中，提取持久的用户偏好和事实（不是临时任务/一次性问题，不要重复已有记忆）。

已有记忆：
{existingText}

对话片段：
{recentEpisodic}

输出 JSON 数组，每条：{{""type"":""UserPreference""或""Knowledge"",""key"":""简短标签"",""content"":""事实/偏好描述""}}
如果没有新的持久信息，返回 []。只输出 JSON，不要其他文字。";

            var client = _chatClientFactory!.Create(null);
            var response = await client.GetResponseAsync(new[]
            {
                new ChatMessage(ChatRole.System, prompt),
                new ChatMessage(ChatRole.User, "提取"),
            }, cancellationToken: cancellationToken).ConfigureAwait(false);

            var json = response.Text?.Trim();
            if (string.IsNullOrEmpty(json) || json == "[]") return;

            // 解析 LLM 输出的 JSON 数组并保存。
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var type = el.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() : "Knowledge";
                var key = el.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
                var content = el.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(content)) continue;

                await _store.SaveAsync(new MemoryItem
                {
                    UserId = userId,
                    Type = Enum.TryParse<MemoryType>(type, out var mt) ? mt : MemoryType.Knowledge,
                    Key = Truncate(key, 40),
                    Content = Truncate(content, 200),
                    Importance = 0.8,
                }, cancellationToken).ConfigureAwait(false);
            }

            _loggerFactory?.CreateLogger<MemoryContextProvider>()?.LogInformation("记忆提炼完成：从 {EpisodicCount} 条 episodic 提取了新 Knowledge/Preference。", episodic.Count);
        }
        catch (Exception ex)
        {
            _loggerFactory?.CreateLogger<MemoryContextProvider>()?.LogWarning(ex, "记忆提炼失败。");
        }
    }

    /// <summary>
    /// 召回并格式化为注入指令块。按类型召回：Knowledge + UserPreference（提炼后的，少而精）+
    /// 最近 5 条 TaskHistory（episodic 上下文）。比全量返回更精准、token 更省。
    /// </summary>
    public async Task<string?> RecallInstructionsAsync(string? userId, CancellationToken cancellationToken)
    {
        var knowledge = await _store.FindByTypeAsync(userId, MemoryType.Knowledge, cancellationToken).ConfigureAwait(false);
        var preferences = await _store.FindByTypeAsync(userId, MemoryType.UserPreference, cancellationToken).ConfigureAwait(false);
        var episodic = await _store.FindByTypeAsync(userId, MemoryType.TaskHistory, cancellationToken).ConfigureAwait(false);

        var items = new List<MemoryItem>();
        items.AddRange(knowledge);
        items.AddRange(preferences);
        items.AddRange(episodic.Take(5)); // 最近 5 条 episodic 作为上下文
        if (items.Count == 0) return null;
        return FormatMemoryBlock(items);
    }

    /// <summary>从本轮请求/响应消息提取用户提问与 agent 回答。</summary>
    public static (string? question, string answer) ExtractTurn(
        IEnumerable<ChatMessage>? requestMessages, IEnumerable<ChatMessage>? responseMessages)
        => (ExtractLastUserText(requestMessages), ExtractAssistantText(responseMessages));

    /// <summary>构造一条 TaskHistory 记忆项；无有效提问时返回 null。</summary>
    public static MemoryItem? BuildEpisodicMemory(string? userId, string? question, string answer)
    {
        if (string.IsNullOrWhiteSpace(question)) return null;
        var content = string.IsNullOrWhiteSpace(answer) ? question! : $"Q: {question}\nA: {answer}";
        return new MemoryItem
        {
            UserId = userId,
            Type = MemoryType.TaskHistory,
            Key = Truncate(question!, 40),
            Content = Truncate(content, 1000),
            Importance = 0.5,
        };
    }

    private static string FormatMemoryBlock(IReadOnlyList<MemoryItem> items)
    {
        // 只注入非空、有意义的记忆项；按类型分组、限制条数避免指令膨胀。
        var lines = items
            .Where(i => !string.IsNullOrWhiteSpace(i.Content))
            .Take(30)
            .Select(i => $"- [{i.Type}] {(string.IsNullOrWhiteSpace(i.Key) ? "" : i.Key + "：")}{i.Content.ReplaceLineEndings(" ")}");
        var text = string.Join("\n", lines);
        return string.IsNullOrEmpty(text) ? "" : "# 用户长期记忆（跨会话保留，供参考）\n" + text;
    }

    private static string? ExtractLastUserText(IEnumerable<ChatMessage>? messages)
    {
        if (messages is null) return null;
        string? last = null;
        foreach (var m in messages)
        {
            if (m.Role == ChatRole.User)
            {
                var t = TextOf(m);
                if (!string.IsNullOrWhiteSpace(t)) last = t;
            }
        }
        return last;
    }

    private static string ExtractAssistantText(IEnumerable<ChatMessage>? messages)
    {
        if (messages is null) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var m in messages)
        {
            if (m.Role == ChatRole.Assistant)
            {
                var t = TextOf(m);
                if (!string.IsNullOrWhiteSpace(t)) sb.AppendLine(t);
            }
        }
        return sb.ToString().Trim();
    }

    private static string? TextOf(ChatMessage m)
    {
        var text = m.Text; // MEAI 聚合所有 TextContent
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
