namespace Aether.AI.Core.Agents;

/// <summary>
/// 一次 agent 运行过程中「值得跨形态传达」的关键节点类型。这是比逐 token 更粗的粒度：
/// 工具起止、审批、计划、完成、错误——这些是不依赖具体 UI（桌面气泡/桌宠/手机/SignalR/未来任何悬浮形态）
/// 的语义节点。逐字答案 token（TextReasoningContent/TextContent 的每一片）是更细的粒度，刻意**不**放这里，
/// 由需要打字机效果的 UI（桌宠）另走细粒度通道，避免「手机端只要进度、不要逐字」的消费者收到噪音。
/// 与 <see cref="ToolStreamEvent"/>/<see cref="TodoItemInfo"/>/<see cref="StreamHandlers"/> 同居 Core 层：
/// 框架无关（不依赖 MEAI），由 Harness/app 构造、由 app/adapter 消费。
/// </summary>
public enum RunProgressKind
{
    /// <summary>模型进入思考（TextReasoningContent 开始）。</summary>
    Reasoning,

    /// <summary>代理开始执行一个工具（FunctionCallContent）。</summary>
    ToolStarted,

    /// <summary>工具执行完毕（FunctionResultContent）。</summary>
    ToolFinished,

    /// <summary>需要用户确认（工具审批请求 ToolApprovalRequestContent）。</summary>
    ApprovalRequested,

    /// <summary>计划更新（todos_* 后 live 待办快照）。</summary>
    PlanUpdated,

    /// <summary>本轮正常结束。</summary>
    RunDone,

    /// <summary>本轮出错。</summary>
    RunError,
}

/// <summary>
/// 运行关键节点的结构化信号。由 <see cref="StreamHandlers"/> 的同一事件源构造、广播给所有「关键信息消费者」
/// （桌宠气泡/手机端 SignalR 进度/未来任何悬浮形态）。纯数据 record，跨层零依赖。
/// 逐字 token **不在**此处（见 <see cref="RunProgressKind"/>）——桌宠的流式打字机是独立细粒度通道。
/// </summary>
public sealed record RunProgress(
    RunProgressKind Kind,
    /// <summary>工具名（ToolStarted/ToolFinished/ApprovalRequested）。</summary>
    string? ToolName = null,
    /// <summary>工具主参数摘要（ToolStarted，如 "600000"）。</summary>
    string? ToolArgument = null,
    /// <summary>文本片段（RunDone 时=本轮摘要、RunError 时=错误信息）。</summary>
    string? Text = null,
    /// <summary>计划步骤数（PlanUpdated）。</summary>
    int PlanStepCount = 0,
    /// <summary>计划快照（PlanUpdated，完整待办列表——手机端进度心跳须保留逐条 ✅/⏳）。</summary>
    IReadOnlyList<TodoItemInfo>? Todos = null);
