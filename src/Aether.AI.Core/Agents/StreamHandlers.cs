namespace Aether.AI.Core.Agents;

/// <summary>
/// 流式运行期间向 UI 上抛各类事件的回调聚合。框架无关（不依赖 MEAI），由 Harness 构造、
/// 由 app/adapter 消费。把原本散落的 onToken/onToolEvent 等参数收拢为一个对象，避免参数爆炸。
/// <para>
/// 各回调含义：
/// <list type="bullet">
///   <item><see cref="OnToken"/>：答案文本（<c>TextContent</c>，即 <c>update.Text</c>）。</item>
///   <item><see cref="OnReasoning"/>：模型思考（<c>TextReasoningContent</c>），与答案分离。</item>
///   <item><see cref="OnToolEvent"/>：工具调用/结果（<c>FunctionCallContent</c>/<c>FunctionResultContent</c>）。</item>
///   <item><see cref="OnApprovalRequest"/>：工具审批请求（<c>ToolApprovalRequestContent</c>）。</item>
///   <item><see cref="OnTodoList"/>：当前待办列表快照（<c>todos_*</c> 调用后读 live 状态）。</item>
///   <item><see cref="OnError"/>：非致命错误（<c>ErrorContent</c>）文本。</item>
///   <item><see cref="OnUsage"/>：token 用量摘要（<c>UsageContent</c>）。</item>
///   <item><see cref="OnProgress"/>：本轮运行的关键节点信号（<see cref="RunProgress"/>）——工具起止/审批/计划/
///     推理/完成/错误。由 Harness 从与其余回调**同一事件源**构造，粒度比逐字 token 粗（见 <see cref="RunProgressKind"/>）。</item>
/// </list>
/// </para>
/// 所有回调均可空，未提供则该类事件被忽略。
/// </summary>
public sealed record StreamHandlers(
    Func<string, CancellationToken, Task>? OnToken = null,
    Func<string, CancellationToken, Task>? OnReasoning = null,
    Func<ToolStreamEvent, CancellationToken, Task>? OnToolEvent = null,
    Func<ToolApprovalRequest, CancellationToken, Task>? OnApprovalRequest = null,
    Func<IReadOnlyList<TodoItemInfo>, CancellationToken, Task>? OnTodoList = null,
    Func<string, CancellationToken, Task>? OnError = null,
    Action<string>? OnUsage = null,
    Action<RunProgress>? OnProgress = null);
