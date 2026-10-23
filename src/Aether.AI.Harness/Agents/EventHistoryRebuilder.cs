using System.Text;
using Aether.AI.Core.Conversations.Runs;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// 从持久化的 <see cref="AgentRun"/> + <see cref="AgentEvent"/> 事件流重建 LLM 的 <see cref="ChatMessage"/> 历史。
/// <para>
/// 取代旧的 <c>TranscriptHistoryConverter.Convert</c>+<c>Sanitize</c> restore saga。事件流是 typed、append-only 的，
/// CallId 在 ToolCall/ToolResult 写入时即配对——故重建出的历史<b>结构不变量由构造保证</b>：
/// </para>
/// <list type="bullet">
///   <item>ToolCall→Assistant(FunctionCallContent) + ToolResult→User(FunctionResultContent) 相邻配对，CallId 一致。</item>
///   <item>同一 assistant 消息内的 TextChunk 与随后的 ToolCall 合并（MEAI 允许一条 assistant 含 TextContent + FunctionCallContent）。</item>
///   <item>ReasoningChunk/PlanUpdated/Approval*/Error/Usage 不进 LLM 历史（思考是私有的、计划是 UI 态、审批是协作元数据）。</item>
///   <item>Interrupted run 尾部无 result 的 ToolCall → 丢弃（唯一保留的小 guard，替代旧的 <c>TrimTrailingNonAssistant</c>）。</item>
///   <item>空 tool 返回 → 占位 <c>(无返回)</c>（防端点拒空 tool_result）。</item>
/// </list>
/// <para>
/// 幂等：对已净化历史再跑无副作用。live 内存路径（<c>RestoringChatHistoryProvider</c> 累积 + <c>SanitizeProviderHistory</c>）不变。
/// </para>
/// </summary>
public static class EventHistoryRebuilder
{
    /// <summary>
    /// 按 run.seq + event.seq 重建 LLM 历史。<paramref name="eventsByRun"/> 按 runId 索引事件列表。
    /// </summary>
    public static List<ChatMessage> RebuildLlmHistory(
        IReadOnlyList<AgentRun> runs,
        IReadOnlyDictionary<Guid, IReadOnlyList<AgentEvent>> eventsByRun)
    {
        var messages = new List<ChatMessage>();
        var pendingText = new StringBuilder();

        foreach (var run in runs.OrderBy(r => r.Seq))
        {
            // 用户请求属于本次执行。
            messages.Add(new ChatMessage(ChatRole.User, run.UserMessage ?? string.Empty));
            pendingText.Clear();

            if (!eventsByRun.TryGetValue(run.Id, out var events) || events is null || events.Count == 0)
                continue;

            foreach (var e in events.OrderBy(x => x.Seq))
            {
                switch (e.EventType)
                {
                    case AgentEventType.TextChunk:
                        var text = AgentEventPayload.Deserialize<AgentEventPayload.TextChunk>(e.Payload)?.Text;
                        if (!string.IsNullOrEmpty(text)) pendingText.Append(text);
                        break;

                    case AgentEventType.ToolCall:
                        var tc = AgentEventPayload.Deserialize<AgentEventPayload.ToolCall>(e.Payload);
                        var asst = new ChatMessage { Role = ChatRole.Assistant };
                        if (pendingText.Length > 0)
                        {
                            asst.Contents.Add(new TextContent(pendingText.ToString()));
                            pendingText.Clear();
                        }
                        // 显式恒等转换：ToInputDict 在写入端已把 null 值合并为 string.Empty（值恒非空），
                        // 但 Dictionary<K,V> 对 IDictionary<K,V'> 泛型不变，MEAI 10.10 起形参放宽为
                        // IDictionary<string, object?>? 后须手动标注（运行时同一类型，可空性擦除）。
                        asst.Contents.Add(new FunctionCallContent(
                            tc?.CallId ?? string.Empty,
                            string.IsNullOrWhiteSpace(tc?.Name) ? "tool" : tc!.Name,
                            (IDictionary<string, object?>?)tc?.Input));
                        messages.Add(asst);
                        break;

                    case AgentEventType.ToolResult:
                        var tr = AgentEventPayload.Deserialize<AgentEventPayload.ToolResult>(e.Payload);
                        // text 不可能在 call 与 result 之间出现（流中 call→result 紧邻），防御性 flush。
                        FlushText(messages, pendingText);
                        var resultMsg = new ChatMessage { Role = ChatRole.User };
                        resultMsg.Contents.Add(new FunctionResultContent(
                            tr?.CallId ?? string.Empty,
                            ResolveOutput(tr?.Output)));
                        messages.Add(resultMsg);
                        break;

                    // 不进 LLM 历史。
                    case AgentEventType.ReasoningChunk:
                    case AgentEventType.PlanUpdated:
                    case AgentEventType.ApprovalRequested:
                    case AgentEventType.ApprovalDecision:
                    case AgentEventType.Error:
                    case AgentEventType.Usage:
                        break;
                }
            }

            // 尾部答案文本（无后续工具调用时）。
            FlushText(messages, pendingText);
        }

        DropOrphanToolCalls(messages);
        return messages;
    }

    private static void FlushText(List<ChatMessage> messages, StringBuilder pendingText)
    {
        if (pendingText.Length == 0) return;
        messages.Add(new ChatMessage(ChatRole.Assistant, pendingText.ToString()));
        pendingText.Clear();
    }

    /// <summary>丢弃无匹配 result 的 tool_call（Interrupted run 尾部孤儿），并移除因此变空的消息。</summary>
    private static void DropOrphanToolCalls(List<ChatMessage> messages)
    {
        var resultIds = new HashSet<string>();
        foreach (var msg in messages)
            foreach (var c in msg.Contents)
                if (c is FunctionResultContent fr) resultIds.Add(fr.CallId ?? string.Empty);

        foreach (var msg in messages)
            for (int i = msg.Contents.Count - 1; i >= 0; i--)
            {
                if (msg.Contents[i] is FunctionCallContent fc
                    && (string.IsNullOrEmpty(fc.CallId) || !resultIds.Contains(fc.CallId)))
                    msg.Contents.RemoveAt(i);
            }

        for (int i = messages.Count - 1; i >= 0; i--)
            if (messages[i].Contents.Count == 0) messages.RemoveAt(i);
    }

    private static object? ResolveOutput(Dictionary<string, object>? output)
    {
        if (output is null || output.Count == 0) return "(无返回)";
        return output;
    }
}
