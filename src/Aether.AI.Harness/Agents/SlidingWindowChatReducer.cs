using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// <see cref="IChatReducer"/> that keeps only the most recent <c>maxHistory</c>
/// messages, implementing a sliding-window memory policy. Wired into the
/// framework's <c>InMemoryChatHistoryProvider</c> at agent build time.
/// </summary>
/// <remarks>
/// 截断起点会调整到「安全 user 起点」（user 角色 + 不含 <see cref="FunctionResultContent"/>）：
/// 朴素 <c>Skip(Count-max)</c> 可能切断 tool_use→tool_result 配对（窗口首条是 tool 结果但它的
/// tool 调用已被截掉）或让首条不是 user 角色——两者都会让 GLM/Anthropic 兼容端点报 messages 非法
/// （如 GLM [1214]）。从 user 文本起点开始可保证首条为 user，且其后每个 tool 结果都有对应的 tool 调用
/// （tool_use 总在 tool_result 之前，起点位于轮次边界则配对完整）。
/// </remarks>
public sealed class SlidingWindowChatReducer : IChatReducer
{
    private readonly int _maxHistory;

    public SlidingWindowChatReducer(int maxHistory)
    {
        if (maxHistory < 1) maxHistory = 1;
        _maxHistory = maxHistory;
    }

    public Task<IEnumerable<ChatMessage>> ReduceAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        if (list.Count <= _maxHistory)
            return Task.FromResult<IEnumerable<ChatMessage>>(list);

        int start = list.Count - _maxHistory;
        start = FindSafeStart(list, start, _maxHistory);
        return Task.FromResult<IEnumerable<ChatMessage>>(list.Skip(start).ToList());
    }

    /// <summary>
    /// 把截断起点调整到一个「安全 user 起点」。优先向前回退（保留近期完整轮次，窗口最多扩到 2×max）；
    /// 回退范围内找不到则向前推进（丢弃边界不完整配对）；仍找不到（如整段为单轮 tool 往返、无 user 文本）
    /// 则不截断——宁可多发历史也不发非法序列。
    /// </summary>
    private static int FindSafeStart(IList<ChatMessage> list, int start, int maxHistory)
    {
        int lowerBound = Math.Max(0, start - maxHistory); // 回退上限：窗口不超过 2×max，避免 tool 密集单轮把窗口拉到很远
        for (int i = start; i >= lowerBound; i--)
        {
            if (IsSafeUserStart(list[i]))
                return i;
        }
        for (int i = start + 1; i < list.Count; i++)
        {
            if (IsSafeUserStart(list[i]))
                return i;
        }
        return 0; // 无安全起点：不截断
    }

    /// <summary>安全起点：user 角色 + 不含 tool 结果（tool 结果的配对 tool 调用可能已被截断）。</summary>
    private static bool IsSafeUserStart(ChatMessage m)
        => m.Role == ChatRole.User
           && !m.Contents.Any(c => c is FunctionResultContent);
}
