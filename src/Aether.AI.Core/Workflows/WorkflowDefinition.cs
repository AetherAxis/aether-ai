namespace Aether.AI.Core.Workflows;

/// <summary>
/// 一条 Handoff 型 workflow 的转交边：from agent → to agent，可选原因说明。
/// </summary>
public sealed class WorkflowHandoff
{
    public Guid FromAgentId { get; init; }
    public Guid ToAgentId { get; init; }
    /// <summary>转交原因（提示 from agent 何时转交给 to agent）。空则用 to agent 的描述。</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// 声明式 workflow 定义：入口 agent + 参与者 + 转交边。框架无关纯数据，
/// 由 Aether.AI.Workflows 的 WorkflowFactory 翻译成 MAF Workflow，由 adapter 持久化到 Notes。
/// </summary>
public sealed class WorkflowDefinition
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string Category { get; init; } = "工作流";

    /// <summary>入口（起始）agent 的 Id。须在 Participants 内。</summary>
    public Guid EntryAgentId { get; init; }

    /// <summary>参与者 agent Id 列表（含入口）。运行时由 adapter 建成 AIAgent。</summary>
    public IReadOnlyList<Guid> Participants { get; init; } = Array.Empty<Guid>();

    /// <summary>转交边列表。</summary>
    public IReadOnlyList<WorkflowHandoff> Handoffs { get; init; } = Array.Empty<WorkflowHandoff>();

    /// <summary>可选：给有转交能力的 agent 的统一转交指令。</summary>
    public string? HandoffInstructions { get; init; }
}
