using System.Runtime.CompilerServices;
using Aether.AI.Core.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

// Framework 与 Core 都有 AgentSession 同名类型；这里只用框架侧（模型聊天线程）。
using FrameworkAgentSession = Microsoft.Agents.AI.AgentSession;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// 协作式工具审批的**请求暂存 + 恢复轮解析**。流冒出 <see cref="Microsoft.Extensions.AI.ToolApprovalRequestContent"/>
/// 时按 frameworkSession（弱键）暂存原请求；审批恢复轮按 RequestId 取回、构造
/// <c>ToolApprovalResponseContent</c>（或 always-approve 包裹）交还调用方，在同一 session 上二次调用
/// 以恢复被审批挂起的工具执行。从 <c>HarnessRuntime</c> 抽出——审批协议的状态与解析自成一体，
/// 核心循环只消费结果。
/// <para>MAF 审批是**协作式**而非回调：流发请求后本轮即结束；宿主带响应消息重调 run 恢复。
/// 暂存表弱键（ConditionalWeakTable），会话被 Evict/回收后条目随 GC 自动清理。</para>
/// </summary>
internal static class ApprovalRecovery
{
    // frameworkSession -> (requestId -> 原 ToolApprovalRequestContent)。
    // 流冒出请求时存入；恢复轮按 RequestId 取回以构造正确关联的响应。会话结束随键自动回收。
    private static readonly ConditionalWeakTable<FrameworkAgentSession, Dictionary<string, ToolApprovalRequestContent>> _pendingApprovals = new();

    /// <summary>流冒出审批请求时暂存原 req，供恢复轮 CreateResponse 使用。</summary>
    internal static void SetPendingApproval(FrameworkAgentSession session, ToolApprovalRequestContent req)
    {
        var dict = _pendingApprovals.GetOrCreateValue(session);
        lock (dict) dict[req.RequestId ?? string.Empty] = req;
    }

    internal static ToolApprovalRequestContent? GetPendingApproval(FrameworkAgentSession session, string requestId)
    {
        if (_pendingApprovals.TryGetValue(session, out var dict))
        {
            lock (dict) return dict.TryGetValue(requestId, out var req) ? req : null;
        }
        return null;
    }

    internal static void RemovePendingApproval(FrameworkAgentSession session, string requestId)
    {
        if (_pendingApprovals.TryGetValue(session, out var dict))
        {
            lock (dict) dict.Remove(requestId);
        }
    }

    /// <summary>
    /// 解析审批恢复轮：取回原请求并构造恢复流（带 <c>ToolApprovalResponseContent</c> 消息）。
    /// 返回 null = 本轮短路、调用方以空文本收敛——两种短路情形的终态信号已在内部发好：
    /// ① 无对应待审请求（可能已被 always-approve 规则自动消化）→ 发 <c>RunDone</c>（本轮的正常收敛，
    /// 否则消费者会因「既无 Done 也无 Error」停在工作中）；
    /// ② 原请求无可绑定的 tool_use（<c>FunctionCallContent</c> + CallId）→ <c>OnError</c> + 发 <c>RunError</c>
    /// （构造出的孤儿 tool result 会让 GLM/Anthropic 兼容端点报 messages 非法，如 GLM [1214]；正常流程
    /// tool_use 是最新 assistant 消息不会被滑窗截断，此守卫兜底暂存被污染/失效的异常情形）。
    /// </summary>
    internal static async Task<IAsyncEnumerable<AgentResponseUpdate>?> TryBeginRecoveryAsync(
        AIAgent agent, FrameworkAgentSession frameworkSession, ToolApprovalDecision approvalResponse,
        StreamHandlers? handlers, IRunProgressSink? progressSink, CancellationToken cancellationToken)
    {
        var req = GetPendingApproval(frameworkSession, approvalResponse.RequestId);
        if (req is null)
        {
            progressSink?.Post(new RunProgress(RunProgressKind.RunDone));
            return null;
        }
        if (req.ToolCall is not FunctionCallContent fc || string.IsNullOrEmpty(fc.CallId))
        {
            RemovePendingApproval(frameworkSession, approvalResponse.RequestId);
            if (handlers?.OnError is not null)
                await handlers.OnError("审批请求已失效（缺少对应的工具调用），请重试。", cancellationToken).ConfigureAwait(false);
            // 本轮无法收敛为正常结果 → 发 RunError 而非 RunDone，消费者显错态而不是误报「搞定」。
            progressSink?.Post(new RunProgress(RunProgressKind.RunError, Text: "审批请求已失效（缺少对应的工具调用）。"));
            return null;
        }
        var reason = approvalResponse.Reason ?? string.Empty;
        AIContent responseContent = approvalResponse.AlwaysApprove
            ? (AIContent)req.CreateAlwaysApproveToolResponse(reason)
            : (AIContent)req.CreateResponse(approvalResponse.Approved, reason);
        RemovePendingApproval(frameworkSession, approvalResponse.RequestId);
        var msg = new ChatMessage { Role = ChatRole.User };
        msg.Contents.Add(responseContent);
        return agent.RunStreamingAsync(new[] { msg }, frameworkSession, cancellationToken: cancellationToken);
    }
}
