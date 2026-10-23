namespace Aether.AI.Core.Conversations.Runs;

/// <summary>
/// 一次 agent 执行内的事件类型。<see cref="AgentEvent.Payload"/> 是按类型解释的 JSON
/// （见 <see cref="AgentEventPayload"/>）。事件 append-only，按 <see cref="AgentEvent.Seq"/> 排序。
/// </summary>
public enum AgentEventType
{
    /// <summary>答案文本片段（<c>TextContent</c> / <c>update.Text</c>）。对应 <see cref="AgentEventPayload.TextChunk"/>。</summary>
    TextChunk,

    /// <summary>模型思考片段（<c>TextReasoningContent</c>），与答案分离。对应 <see cref="AgentEventPayload.ReasoningChunk"/>。</summary>
    ReasoningChunk,

    /// <summary>工具调用（<c>FunctionCallContent</c>）。对应 <see cref="AgentEventPayload.ToolCall"/>。CallId 在写入时即有。</summary>
    ToolCall,

    /// <summary>工具结果（<c>FunctionResultContent</c>）。对应 <see cref="AgentEventPayload.ToolResult"/>。与 ToolCall 按 CallId 配对。</summary>
    ToolResult,

    /// <summary>计划/待办列表快照（<c>todos_*</c> 后的 live 状态）。对应 <see cref="AgentEventPayload.PlanUpdated"/>。</summary>
    PlanUpdated,

    /// <summary>工具审批请求（<c>ToolApprovalRequestContent</c>）。对应 <see cref="AgentEventPayload.ApprovalRequested"/>。</summary>
    ApprovalRequested,

    /// <summary>用户对审批请求的决定。对应 <see cref="AgentEventPayload.ApprovalDecision"/>。</summary>
    ApprovalDecision,

    /// <summary>非致命错误（<c>ErrorContent</c>）。对应 <see cref="AgentEventPayload.Error"/>。</summary>
    Error,

    /// <summary>token 用量摘要（<c>UsageContent</c>）。对应 <see cref="AgentEventPayload.Usage"/>。</summary>
    Usage,
}

/// <summary>
/// 执行轨迹中的单个 typed 事件。append-only——写入即不可变，CallId 在 ToolCall/ToolResult 写入时即配对。
/// <para>
/// 这是持久化的最小单元：恢复时按 run.seq + event.seq 顺序读出，重建 LLM 历史与 UI transcript。
/// 结构不变量（call↔result 配对、role 交替）由写入端保证，恢复端无需 sanitize。
/// </para>
/// </summary>
public sealed class AgentEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid RunId { get; init; }

    /// <summary>run 内事件序号（从 1 起），决定事件顺序。</summary>
    public int Seq { get; init; }

    public AgentEventType EventType { get; init; }

    /// <summary>按 <see cref="EventType"/> 解释的 JSON payload（见 <see cref="AgentEventPayload"/>）。</summary>
    public string Payload { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
