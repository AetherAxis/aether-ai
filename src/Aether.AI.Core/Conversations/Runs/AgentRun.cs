namespace Aether.AI.Core.Conversations.Runs;

/// <summary>
/// 一次 agent 执行的生命周期状态。<see cref="AgentRun"/> 持有此值；事件流按 run 聚合。
/// </summary>
public enum RunStatus
{
    /// <summary>运行中（流式尚未结束）。</summary>
    Running,

    /// <summary>正常完成（已产出最终答案）。</summary>
    Completed,

    /// <summary>运行失败（异常）。</summary>
    Failed,

    /// <summary>被中断（审批挂起未恢复 / 用户停止 / 进程退出）。尾部可能有未配对的 ToolCall 事件。</summary>
    Interrupted,
}

/// <summary>
/// 一次任务执行（用户发送 → agent 完成）。用户请求（<see cref="UserMessage"/>）属于这次执行；
/// 执行轨迹是 <see cref="AgentEvent"/> 的 append-only 事件流，按 <see cref="Seq"/> 排序。
/// <para>
/// 取代旧的 flat <c>Message</c> transcript：工具调用/结果/思考/计划/审批不再是压扁的 Message 行，
/// 而是 typed 事件，CallId 在写入时即配对——跨重启重建 LLM 历史与 UI transcript 时结构不变量由构造保证。
/// </para>
/// </summary>
public sealed class AgentRun
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid ConversationId { get; init; }

    /// <summary>会话内 run 的序号（从 1 起），决定 transcript 顺序。</summary>
    public int Seq { get; init; }

    public RunStatus Status { get; set; } = RunStatus.Running;

    /// <summary>本次执行绑定的 agent 逻辑 id（冗余于 Conversation.AgentId，便于按 run 查询）。</summary>
    public string AgentId { get; init; } = string.Empty;

    public string AgentName { get; init; } = string.Empty;

    /// <summary>本次执行使用的模型 id（可空——agent 不绑模型时为空）。</summary>
    public string? ModelId { get; init; }

    /// <summary>触发本次执行的用户消息原文。</summary>
    public string UserMessage { get; init; } = string.Empty;

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? FinishedAt { get; set; }
}
