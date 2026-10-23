using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// 活路径 LLM 历史净化（<b>非</b>持久化恢复路径）。持久化恢复路径已改用
/// <see cref="EventHistoryRebuilder.RebuildLlmHistory"/> 从 typed 事件流重建——结构不变量由构造保证，
/// 不再需要 restore 净化 saga（旧的 <c>Convert</c>/<c>Sanitize</c> 已删）。
/// <para>
/// 此处保留两个「活路径」净化函数，防的是<b>运行时内存</b>孤儿（与持久化无关）：
/// </para>
/// <list type="bullet">
///   <item><see cref="SanitizeLive"/>：包装 LLM 客户端，每次发请求前就地修正 tool 消息结构
///   （修 DeepSeek 等 OpenAI 兼容端点「Messages with role tool must be a response to a preceding
///   message with tool_calls」400）。不裁尾、不合并——活路径工具续轮合法地以 tool_result 收尾。</item>
///   <item><see cref="SanitizeProviderHistory"/>：<c>RestoringChatHistoryProvider</c> 累积路径专用。
///   与 <see cref="SanitizeLive"/> 关键区别：<b>不丢弃孤儿 tool_call</b>，只丢孤儿 tool_result
///   （活路径当前轮的 call 尚无 result 是正常的，误删致工具重调——见 double-tool-execution-fix）。</item>
/// </list>
/// </summary>
public static class TranscriptHistoryConverter
{
    /// <summary>
    /// <see cref="Sanitize"/> 的「活路径」子集，用于包装 LLM 客户端、在每次发请求前就地修正
    /// tool 消息结构（修 DeepSeek 等 OpenAI 兼容端点的「Messages with role tool must be a
    /// response to a preceding message with tool_calls」400）。
    /// <para>与 restore 净化的关键区别：<b>不做</b>合并连续同 role 与裁剪尾部非 assistant——活路径的
    /// 工具续轮请求合法地以 tool_result 消息收尾（让 LLM 看到工具返回继续生成），裁剪尾部会删掉这个
    /// tool_result、令 LLM 收不到工具结果而空转。活路径只需修「结构非法」的孤儿。</para>
    /// <para>幂等：对已合法历史无副作用（配对完整的 tool_use/tool_result 原样放行）。</para>
    /// </summary>
    public static List<ChatMessage> SanitizeLive(IEnumerable<ChatMessage>? messages)
    {
        var list = (messages ?? Enumerable.Empty<ChatMessage>()).Select(Clone).ToList();
        if (list.Count == 0) return list;

        SynthesizeEmptyCallIds(list);
        DropOrphanToolContents(list);
        FixEmptyToolResults(list);
        RemoveEmptyMessages(list);
        return list;
    }

    /// <summary>
    /// <see cref="RestoringChatHistoryProvider.ProvideChatHistoryAsync"/> 专用活路径净化。与
    /// <see cref="SanitizeLive"/> 的关键区别：<b>不丢弃孤儿 tool_call</b>，只丢弃孤儿 tool_result。
    /// <para>
    /// 活路径 FICC 循环里，<see cref="RestoringChatHistoryProvider.StoreChatHistoryAsync"/> 在每轮 LLM
    /// 调用<em>之后</em>才落地消息——故 <c>ProvideChatHistoryAsync</c> 被调用时，最近一轮的 tool_call
    /// 必然<em>尚无</em>配对 result（result 要等下一轮 Store 从 FICC 追加的请求消息里落地）。若把这个
    /// 「当前进行中」的 call 当孤儿丢弃，下一轮请求就变成「有 tool_result 无前置 tool_call」→ GLM 见不到
    /// 自己发过的调用 → 重调同一工具（每个工具执行两遍）。
    /// </para>
    /// <para>故此处只修真正结构非法的孤儿 tool_result（无前置 call——OpenAI 兼容端点会 400），保留所有
    /// tool_call。</para>
    /// </summary>
    public static List<ChatMessage> SanitizeProviderHistory(IEnumerable<ChatMessage>? messages)
    {
        var list = (messages ?? Enumerable.Empty<ChatMessage>()).Select(Clone).ToList();
        if (list.Count == 0) return list;

        SynthesizeEmptyCallIds(list);
        DropOrphanToolResults(list);
        FixEmptyToolResults(list);
        RemoveEmptyMessages(list);
        return list;
    }

    /// <summary>只丢弃孤儿 tool_result（无匹配 call），<b>保留所有 tool_call</b>。
    /// 与 <see cref="DropOrphanToolContents"/> 的区别：不丢 tool_call——活路径当前轮的 call 尚无 result 是正常的。</summary>
    private static void DropOrphanToolResults(List<ChatMessage> list)
    {
        var callIds = new HashSet<string>();
        foreach (var msg in list)
            foreach (var c in msg.Contents)
                if (c is FunctionCallContent fc) callIds.Add(fc.CallId ?? string.Empty);

        foreach (var msg in list)
            for (int i = msg.Contents.Count - 1; i >= 0; i--)
            {
                if (msg.Contents[i] is FunctionResultContent fr
                    && (string.IsNullOrEmpty(fr.CallId) || !callIds.Contains(fr.CallId)))
                    msg.Contents.RemoveAt(i);
            }
    }

    // 浅拷贝：净化会就地改 Contents（删/替换 content），不能改到调用方持有的原始消息。
    private static ChatMessage Clone(ChatMessage src)
    {
        var copy = new ChatMessage { Role = src.Role };
        foreach (var c in src.Contents) copy.Contents.Add(c);
        return copy;
    }

    /// <summary>空 CallId 合成：按出现顺序把第 N 个空 call 与第 N 个空 result 配对，赋同一合成 id。
    /// CallId 在 MEAI 中是 init-only，故重建 content 对象替换。多余的空 call/result 保持空 id，下一步当孤儿丢弃。</summary>
    private static void SynthesizeEmptyCallIds(List<ChatMessage> list)
    {
        var emptyCalls = new List<(ChatMessage msg, int idx, FunctionCallContent fc)>();
        var emptyResults = new List<(ChatMessage msg, int idx, FunctionResultContent fr)>();
        foreach (var msg in list)
            for (int i = 0; i < msg.Contents.Count; i++)
            {
                switch (msg.Contents[i])
                {
                    case FunctionCallContent fc when string.IsNullOrEmpty(fc.CallId):
                        emptyCalls.Add((msg, i, fc)); break;
                    case FunctionResultContent fr when string.IsNullOrEmpty(fr.CallId):
                        emptyResults.Add((msg, i, fr)); break;
                }
            }
        int n = Math.Min(emptyCalls.Count, emptyResults.Count);
        for (int i = 0; i < n; i++)
        {
            var id = $"toolu_synth_{i}";
            var (cmsg, ci, fc) = emptyCalls[i];
            cmsg.Contents[ci] = new FunctionCallContent(id, fc.Name, fc.Arguments);
            var (rmsg, ri, fr) = emptyResults[i];
            rmsg.Contents[ri] = new FunctionResultContent(id, fr.Result);
        }
    }

    /// <summary>丢弃孤儿：无匹配 result 的 tool_use / 无匹配 call 的 tool_result（就地删 content）。</summary>
    private static void DropOrphanToolContents(List<ChatMessage> list)
    {
        var callIds = new HashSet<string>();
        var resultIds = new HashSet<string>();
        foreach (var msg in list)
            foreach (var c in msg.Contents)
            {
                if (c is FunctionCallContent fc) callIds.Add(fc.CallId ?? string.Empty);
                else if (c is FunctionResultContent fr) resultIds.Add(fr.CallId ?? string.Empty);
            }
        foreach (var msg in list)
            for (int i = msg.Contents.Count - 1; i >= 0; i--)
            {
                switch (msg.Contents[i])
                {
                    case FunctionCallContent fc when !resultIds.Contains(fc.CallId ?? string.Empty) || string.IsNullOrEmpty(fc.CallId):
                    case FunctionResultContent fr when !callIds.Contains(fr.CallId ?? string.Empty) || string.IsNullOrEmpty(fr.CallId):
                        msg.Contents.RemoveAt(i);
                        break;
                }
            }
    }

    /// <summary>空/null 工具返回 → 占位字符串（避免 tool_result content 为空被端点拒）。</summary>
    private static void FixEmptyToolResults(List<ChatMessage> list)
    {
        foreach (var msg in list)
            for (int i = 0; i < msg.Contents.Count; i++)
            {
                if (msg.Contents[i] is FunctionResultContent fr && IsEmptyResult(fr.Result))
                    msg.Contents[i] = new FunctionResultContent(fr.CallId, "(无返回)");
            }
    }

    private static bool IsEmptyResult(object? result)
        => result is null || (result is string s && string.IsNullOrWhiteSpace(s));

    /// <summary>移除过滤后无 content 的消息。</summary>
    private static void RemoveEmptyMessages(List<ChatMessage> list)
    {
        for (int i = list.Count - 1; i >= 0; i--)
            if (list[i].Contents.Count == 0) list.RemoveAt(i);
    }
}
