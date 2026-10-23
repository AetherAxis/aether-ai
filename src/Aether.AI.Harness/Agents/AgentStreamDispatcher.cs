using System.Diagnostics;
using Aether.AI.Core.Agents;
using Aether.AI.Harness.Telemetry;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

// Framework 与 Core 都有 AgentSession 同名类型；这里只用框架侧（模型聊天线程）。
using FrameworkAgentSession = Microsoft.Agents.AI.AgentSession;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// 单轮流式 run 的内容分发器：消费 <see cref="AIAgent.RunStreamingAsync"/> 的
/// <see cref="AgentResponseUpdate"/> 流，把答案 token 与每类 <see cref="AIContent"/>（工具调用/结果、
/// 推理、审批请求、待办、非致命错误、用量）分发到 <see cref="StreamHandlers"/> 回调、OTel Activity 事件、
/// 与关键节点 <see cref="RunProgress"/> 双通道（在场 handlers.OnProgress + 全局 <see cref="IRunProgressSink"/>）。
/// <para>从 <c>HarnessRuntime.RunStreamingCoreAsync</c> 抽出——核心只保留 ambient 上下文/OTel 围栏/审批恢复
/// 选流，「每个 update 怎么解释」集中在这一处。一个实例对应一轮 run（持答案文本累积 + callId→工具名表），
/// 不可跨 run 复用。</para>
/// </summary>
internal sealed class AgentStreamDispatcher
{
    private readonly AIAgent _agent;
    private readonly FrameworkAgentSession _frameworkSession;
    private readonly StreamHandlers? _handlers;
    private readonly Action<string>? _onToolCallName;
    private readonly Activity? _activity;
    private readonly IRunProgressSink? _progressSink;

    // callId -> 工具名：FunctionResultContent 无 Name 字段，靠 CallId 回查调用时记录的名称。
    private readonly Dictionary<string, string> _toolNamesByCallId = new();
    private readonly System.Text.StringBuilder _sb = new();

    public AgentStreamDispatcher(
        AIAgent agent, FrameworkAgentSession frameworkSession,
        StreamHandlers? handlers, Action<string>? onToolCallName,
        Activity? activity, IRunProgressSink? progressSink)
    {
        _agent = agent;
        _frameworkSession = frameworkSession;
        _handlers = handlers;
        _onToolCallName = onToolCallName;
        _activity = activity;
        _progressSink = progressSink;
    }

    /// <summary>本轮 run 累积的答案文本（update.Text 仅聚合 TextContent，不含推理）。</summary>
    public string Text => _sb.ToString();

    /// <summary>消费整个流：每个 update 先上抛答案 token，再按 <see cref="AIContent"/> 类型逐条分发。</summary>
    public async Task DispatchAsync(IAsyncEnumerable<AgentResponseUpdate> stream, CancellationToken cancellationToken)
    {
        await foreach (var update in stream.ConfigureAwait(false))
        {
            // 答案文本（update.Text 仅聚合 TextContent，不含推理）。
            if (!string.IsNullOrEmpty(update.Text))
            {
                _sb.Append(update.Text);
                if (_handlers?.OnToken is not null)
                    await _handlers.OnToken(update.Text, cancellationToken).ConfigureAwait(false);
            }

            if (update.Contents is null) continue;

            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent call when !string.IsNullOrEmpty(call.Name):
                        if (!string.IsNullOrEmpty(call.CallId))
                            _toolNamesByCallId[call.CallId] = call.Name!;
                        _onToolCallName?.Invoke(call.Name!);
                        _activity?.AddEvent(new ActivityEvent(AetherActivitySource.EventToolCall,
                            tags: new ActivityTagsCollection(new[] { new KeyValuePair<string, object?>("name", call.Name), new KeyValuePair<string, object?>("callId", call.CallId) })));
                        if (_handlers?.OnToolEvent is not null)
                        {
                            await _handlers.OnToolEvent(
                                new ToolStreamEvent(ToolStreamEventKind.Call, call.Name!, call.CallId ?? string.Empty,
                                    StreamContentFormat.ToInputDict(call.Arguments), null),
                                cancellationToken).ConfigureAwait(false);
                        }
                        // 关键节点：工具开始（与 OnToolEvent 同一事件源，粒度更粗；轮询去重在 Fanout 里）。
                        EmitProgress(new RunProgress(RunProgressKind.ToolStarted,
                            ToolName: call.Name!, ToolArgument: StreamContentFormat.FirstArgSummary(call.Arguments)));
                        break;

                    case FunctionResultContent result:
                    {
                        var name = (result.CallId is not null && _toolNamesByCallId.TryGetValue(result.CallId, out var n))
                            ? n : "工具";
                        _activity?.AddEvent(new ActivityEvent(AetherActivitySource.EventToolResult,
                            tags: new ActivityTagsCollection(new[] { new KeyValuePair<string, object?>("name", name), new KeyValuePair<string, object?>("callId", result.CallId) })));
                        if (_handlers?.OnToolEvent is not null)
                        {
                            await _handlers.OnToolEvent(
                                new ToolStreamEvent(ToolStreamEventKind.Result, name, result.CallId ?? string.Empty,
                                    null, StreamContentFormat.ToOutputDict(result.Result)),
                                cancellationToken).ConfigureAwait(false);
                        }
                        // todos_* 调用后读 live 待办列表上抛（计划 UI）。
                        // 放宽匹配：MAF 版本不同工具名可能为 todos_add / todos / todo_add 等。
                        if (name.StartsWith("todo", StringComparison.OrdinalIgnoreCase) && _handlers is not null)
                        {
                            var todos = await ReadTodoListAsync(cancellationToken).ConfigureAwait(false);
                            if (todos is not null)
                            {
                                if (_handlers.OnTodoList is not null)
                                    await _handlers.OnTodoList(todos, cancellationToken).ConfigureAwait(false);
                                // 关键节点：计划更新（带步骤数 + 全量快照供消费者渲染 ✅/⏳）。
                                // 与 OnTodoList 同一事件源，但独立于它——未订阅 OnTodoList 的消费者（Automation 等）也收得到。
                                EmitProgress(new RunProgress(RunProgressKind.PlanUpdated,
                                    PlanStepCount: todos.Count, Todos: todos));
                            }
                        }
                        // 关键节点：工具结束。
                        EmitProgress(new RunProgress(RunProgressKind.ToolFinished, ToolName: name));
                        break;
                    }

                    // 内置 web search（EnableWebSearch 开启时，hosted 服务发起的搜索）。与普通工具同走
                    // ToolStreamEvent 路径——两者皆继承自 ToolCallContent/ToolResultContent 故带 CallId，
                    // 可按 CallId 配对（桌面合并成一卡、手机两卡），复用既有工具卡渲染/持久化/重启重建管线，
                    // 不静默丢。OpenAI 内置 web search 实际多走 TextContent（已由 OnToken 处理），此分支兜底
                    // provider 直接发结构化 WebSearch 内容的少见情形。
                    case WebSearchToolCallContent wsCall:
                    {
                        var query = wsCall.Queries is null ? string.Empty : string.Join(" | ", wsCall.Queries);
                        if (!string.IsNullOrEmpty(wsCall.CallId))
                            _toolNamesByCallId[wsCall.CallId] = StreamContentFormat.WebSearchToolName;
                        _activity?.AddEvent(new ActivityEvent(AetherActivitySource.EventToolCall,
                            tags: new ActivityTagsCollection(new[] { new KeyValuePair<string, object?>("name", StreamContentFormat.WebSearchToolName), new KeyValuePair<string, object?>("callId", wsCall.CallId) })));
                        if (_handlers?.OnToolEvent is not null)
                        {
                            await _handlers.OnToolEvent(
                                new ToolStreamEvent(ToolStreamEventKind.Call, StreamContentFormat.WebSearchToolName, wsCall.CallId ?? string.Empty,
                                    string.IsNullOrEmpty(query) ? null : new Dictionary<string, object> { ["query"] = query }, null),
                                cancellationToken).ConfigureAwait(false);
                        }
                        break;
                    }

                    case WebSearchToolResultContent wsResult:
                    {
                        var name = (wsResult.CallId is not null && _toolNamesByCallId.TryGetValue(wsResult.CallId, out var n))
                            ? n : StreamContentFormat.WebSearchToolName;
                        _activity?.AddEvent(new ActivityEvent(AetherActivitySource.EventToolResult,
                            tags: new ActivityTagsCollection(new[] { new KeyValuePair<string, object?>("name", name), new KeyValuePair<string, object?>("callId", wsResult.CallId) })));
                        if (_handlers?.OnToolEvent is not null)
                        {
                            await _handlers.OnToolEvent(
                                new ToolStreamEvent(ToolStreamEventKind.Result, name, wsResult.CallId ?? string.Empty,
                                    null, StreamContentFormat.ToWebSearchOutputDict(wsResult.Outputs)),
                                cancellationToken).ConfigureAwait(false);
                        }
                        break;
                    }

                    case TextReasoningContent tr when !string.IsNullOrEmpty(tr.Text):
                        if (_handlers?.OnReasoning is not null)
                            await _handlers.OnReasoning(tr.Text!, cancellationToken).ConfigureAwait(false);
                        // 关键节点：进入思考（逐字 token 不走此处，见 RunProgressKind 粒度约定）。
                        EmitProgress(new RunProgress(RunProgressKind.Reasoning));
                        break;

                    case ToolApprovalRequestContent approvalReq:
                        // 暂存原 req，供恢复轮 CreateResponse 使用（ApprovalRecovery 弱键表）。
                        ApprovalRecovery.SetPendingApproval(_frameworkSession, approvalReq);
                        _activity?.AddEvent(new ActivityEvent(AetherActivitySource.EventApproval,
                            tags: new ActivityTagsCollection(new[] { new KeyValuePair<string, object?>("requestId", approvalReq.RequestId) })));
                        if (_handlers?.OnApprovalRequest is not null)
                        {
                            var fc = approvalReq.ToolCall as FunctionCallContent;
                            await _handlers.OnApprovalRequest(
                                new ToolApprovalRequest(
                                    approvalReq.RequestId ?? string.Empty,
                                    fc?.Name ?? "工具",
                                    approvalReq.ToolCall?.CallId ?? string.Empty,
                                    fc is not null ? StreamContentFormat.ToInputDict(fc.Arguments) : null),
                                cancellationToken).ConfigureAwait(false);
                        }
                        // 关键节点：审批请求（危险操作需确认）。
                        EmitProgress(new RunProgress(RunProgressKind.ApprovalRequested,
                            ToolName: (approvalReq.ToolCall as FunctionCallContent)?.Name));
                        break;

                    case ErrorContent err when !string.IsNullOrEmpty(err.Message):
                        _activity?.AddEvent(new ActivityEvent(AetherActivitySource.EventError,
                            tags: new ActivityTagsCollection(new[] { new KeyValuePair<string, object?>("message", err.Message) })));
                        if (_handlers?.OnError is not null)
                            await _handlers.OnError(err.Message!, cancellationToken).ConfigureAwait(false);
                        // 关键节点：本轮出错（非致命 ErrorContent）。
                        EmitProgress(new RunProgress(RunProgressKind.RunError, Text: err.Message));
                        break;

                    case UsageContent usage:
                        _activity?.AddEvent(new ActivityEvent(AetherActivitySource.EventUsage,
                            tags: new ActivityTagsCollection(new[] { new KeyValuePair<string, object?>("summary", StreamContentFormat.SummarizeUsage(usage)) })));
                        _handlers?.OnUsage?.Invoke(StreamContentFormat.SummarizeUsage(usage));
                        break;
                }
            }
        }
    }

    /// <summary>读 TodoProvider 当前 live 待办列表并映射为 <see cref="TodoItemInfo"/>。
    /// agent 未启用 TodoProvider 时返回 null（调用方据此跳过上抛）。</summary>
    private async Task<IReadOnlyList<TodoItemInfo>?> ReadTodoListAsync(CancellationToken ct)
    {
        var provider = _agent.GetService<TodoProvider>();
        if (provider is null) return null;
        var items = await provider.GetAllTodosAsync(_frameworkSession, ct).ConfigureAwait(false);
        return items
            .Select(i => new TodoItemInfo(i.Id.ToString(), i.Title ?? string.Empty, i.Description, i.IsComplete))
            .ToList();
    }

    /// <summary>把一条关键节点信号同时送进本轮 run 的 <see cref="StreamHandlers.OnProgress"/>（在场消费者，
    /// 如聊天 VM）与 HarnessRuntime 注入的全局 <see cref="IRunProgressSink"/>（如桌宠路由器）。
    /// <para>**两条通道缺一不可**：只在 <c>handlers</c> 里发，则 Automation 定时任务/工作流这类
    /// 不传 handlers（或只传 OnToken）的调用方什么都收不到，「信号下沉 Harness」即落空；
    /// 只在 sink 里发，则未来按 round 订阅的消费者拿不到本轮节点。</para>
    /// <para>去重由 <see cref="RunProgressFanout"/> 在 sink 侧统一处理（两道通道最终多汇到同一 fanout，
    /// 连续同节点会被折叠一次），故此处不做判断。</para></summary>
    private void EmitProgress(RunProgress signal)
    {
        _handlers?.OnProgress?.Invoke(signal);
        _progressSink?.Post(signal);
    }
}
