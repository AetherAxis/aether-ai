using Aether.AI.Core.Agents;
using Aether.AI.Core.Messages;
using Aether.AI.Core.Sessions;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Sessions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

// Both the framework and Aether.AI.Core define a type named AgentSession.
// Alias them to avoid ambiguity: the framework's is the model-side chat
// thread; Aether's is the runtime execution context.
using FrameworkAgentSession = Microsoft.Agents.AI.AgentSession;
using AetherSession = Aether.AI.Core.Sessions.AgentSession;

namespace Aether.AI.Harness;

/// <summary>
/// The Aether runtime: turns registered <see cref="AgentDefinition"/>s into
/// live agents, owns session lifecycle, and drives multi-turn execution on top
/// of Microsoft Agent Framework.
/// <para>
/// Responsibilities:
/// <list type="bullet">
///   <item>Agent creation + caching (delegated to <see cref="AgentFactory"/> via the internal
///   <see cref="AgentRuntimeRegistry"/>, which also owns the per-conversation framework sessions).</item>
///   <item>Session management (delegated to <see cref="SessionManager"/>).</item>
///   <item>Multi-turn conversation via the framework's <c>AgentSession</c> thread (agent path keyed by
///   conversation id; the workflow path brings its own <c>AIAgent</c> + session and skips Aether bookkeeping).</item>
///   <item>Streaming execution: both <c>RunStreamingAsync</c> overloads share the static
///   <c>RunStreamingCoreAsync</c> loop, which delegates per-update content dispatch to
///   <see cref="AgentStreamDispatcher"/> (every <see cref="AIContent"/> kind → <see cref="StreamHandlers"/>,
///   key-node <see cref="RunProgress"/> signals dual-channel), drives the cooperative tool-approval
///   re-invocation protocol via <see cref="ApprovalRecovery"/> (stashing pending requests by
///   <c>RequestId</c>), and formats tool payloads via <see cref="StreamContentFormat"/>.</item>
///   <item>Lifecycle: <see cref="Invalidate"/> (agent edited → drop cache), <see cref="RebuildSessionFramework"/>
///   (keep conversation, rebuild framework session), <see cref="EvictSession"/> (conversation deleted → drop
///   runtime state; weak-keyed provider caches follow via GC).</item>
/// </list>
/// </para>
/// </summary>
public sealed class HarnessRuntime
{
    private readonly AgentRuntimeRegistry _registry;
    private readonly SessionManager _sessions;
    private readonly IRunProgressSink? _progressSink;

    public HarnessRuntime(AgentFactory factory, SessionManager sessions,
        SessionConversationMap? sessionConversationMap = null, IRunProgressSink? progressSink = null)
    {
        _registry = new AgentRuntimeRegistry(
            factory ?? throw new ArgumentNullException(nameof(factory)),
            sessionConversationMap ?? new SessionConversationMap());
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _progressSink = progressSink;
    }

    public SessionManager Sessions => _sessions;

    /// <summary>Registers an agent definition so it can be started by id.</summary>
    public HarnessRuntime RegisterAgent(AgentDefinition definition)
    {
        _registry.Register(definition);
        return this;
    }

    /// <summary>True if an agent with the given id is registered.</summary>
    public bool HasAgent(string agentId) => _registry.Has(agentId);

    /// <summary>
    /// 使已注册 agent 的缓存失效：移除其定义与已构建的 agent 实例，下次运行时重建。
    /// 用于自定义 Agent 被编辑后丢弃旧配置。会话线程由调用方（runner）丢弃并新建。
    /// </summary>
    public HarnessRuntime Invalidate(string agentId)
    {
        _registry.Invalidate(agentId);
        return this;
    }

    /// <summary>Starts a new runtime session for the named agent.</summary>
    public AetherSession StartSession(string agentId)
    {
        var definition = _registry.Resolve(agentId);
        return _sessions.Create(definition.Id, definition.Name);
    }

    // —— AetherSession 簿记三件套（RunAsync 与 RunStreamingAsync(Guid,…) 共用）：每轮 run 的状态迁移
    //    必须成套发生（Active 起、Faulted/Idle 收、每次迁移 Touch），集中在三个 helper 防两方法漂移。

    /// <summary>开轮：记录用户消息 + <see cref="SessionState.Active"/>。审批恢复轮不追加用户消息
    /// （首轮已记录；恢复轮带的是审批响应而非新用户输入）。</summary>
    private static void BeginTurn(AetherSession session, string userMessage, bool isRecoveryTurn)
    {
        if (!isRecoveryTurn)
            session.Messages.Add(Message.User(userMessage));
        session.State = SessionState.Active;
        session.Touch();
    }

    /// <summary>失败轮：<see cref="SessionState.Faulted"/> + lastError。**不发 RunError**——
    /// RunAsync（非流式）历来不发进度信号，仅 RunStreamingAsync 的 catch 在调本方法后另发，
    /// 勿在此「统一」以免改变两条路径的既有行为。</summary>
    private static void FailTurn(AetherSession session, Exception ex)
    {
        session.State = SessionState.Faulted;
        session.Properties["lastError"] = ex.Message;
        session.Touch();
    }

    /// <summary>收轮：记录助手回复 + <see cref="SessionState.Idle"/>。</summary>
    private static void CompleteTurn(AetherSession session, string assistantText)
    {
        session.Messages.Add(Message.Assistant(assistantText));
        session.State = SessionState.Idle;
        session.Touch();
    }

    /// <summary>
    /// Runs one turn of the agent bound to <paramref name="sessionId"/>,
    /// sending <paramref name="userMessage"/> and returning the assistant's
    /// textual response. Prior turns in the same session are remembered.
    /// </summary>
    public async Task<string> RunAsync(
        Guid sessionId,
        string userMessage,
        CancellationToken cancellationToken = default)
    {
        var session = _sessions.Get(sessionId)
            ?? throw new InvalidOperationException($"Session '{sessionId}' not found.");

        var definition = _registry.Resolve(session.AgentId);
        var agent = _registry.GetOrCreateAgent(definition);
        var frameworkSession = await _registry.GetOrCreateFrameworkSessionAsync(sessionId, agent, cancellationToken);

        BeginTurn(session, userMessage, isRecoveryTurn: false);

        AgentResponse response;
        try
        {
            response = await agent.RunAsync(userMessage, frameworkSession, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            FailTurn(session, ex);
            throw;
        }

        var assistantText = response.Text ?? string.Empty;

        // Record any tool invocations the framework performed this turn.
        foreach (var toolName in ExtractToolCalls(response))
        {
            session.ToolCalls.Add(toolName);
        }

        CompleteTurn(session, assistantText);
        return assistantText;
    }

    /// <summary>
    /// Runs one turn as a streaming response. 答案文本经 <see cref="StreamHandlers.OnToken"/> 流式上抛，
    /// 工具调用/结果、模型思考(<c>TextReasoningContent</c>)、审批请求、待办列表、非致命错误、用量等
    /// 经 <paramref name="handlers"/> 的各回调上抛。<paramref name="approvalResponse"/> 非空表示
    /// 审批恢复轮（带 <c>ToolApprovalResponseContent</c> 二次调用以恢复被审批挂起的工具执行）。
    /// </summary>
    public async Task<string> RunStreamingAsync(
        Guid sessionId,
        string userMessage,
        StreamHandlers? handlers = null,
        ToolApprovalDecision? approvalResponse = null,
        string? desiredMode = null,
        CancellationToken cancellationToken = default)
    {
        var session = _sessions.Get(sessionId)
            ?? throw new InvalidOperationException($"Session '{sessionId}' not found.");

        var definition = _registry.Resolve(session.AgentId);
        var agent = _registry.GetOrCreateAgent(definition);
        var frameworkSession = await _registry.GetOrCreateFrameworkSessionAsync(sessionId, agent, cancellationToken);

        // 运行前应用期望模式（plan/execute 等）；会话新建或已存在均生效。null=不改变当前模式。
        if (desiredMode is not null)
            await TrySetModeAsync(agent, frameworkSession, desiredMode, cancellationToken).ConfigureAwait(false);

        // 审批恢复轮不追加用户消息（首轮已记录），由 BeginTurn 内部判断。
        BeginTurn(session, userMessage, isRecoveryTurn: approvalResponse is not null);

        string fullText;
        try
        {
            fullText = await RunStreamingCoreAsync(agent, frameworkSession, userMessage, handlers, approvalResponse, cancellationToken,
                name => session.ToolCalls.Add(name), _progressSink,
                emitTelemetry: definition.EnableOpenTelemetry, agentId: definition.Id, conversationId: sessionId.ToString(), mode: desiredMode);
        }
        catch (Exception ex)
        {
            // 镜像 RunAsync 的 Faulted 分支：流式 run 里 provider 网络错误/内部异常若不兜住，
            // 状态永远停在 Active（Idle 只在正常路径赋值）→ 后续轮次/UI 误判会话仍在跑。
            FailTurn(session, ex);
            // 本轮以异常收敛（未走到核心的正常返回）→ 进度消费者须收到 RunError，
            // 否则桌宠等形态会永远停在「工作」态。信号源下沉后此处由 Harness 统一兜底。
            // （RunAsync 不发 RunError——非流式路径历来无进度消费者，见 FailTurn 注释。）
            _progressSink?.Post(new RunProgress(RunProgressKind.RunError, Text: ex.Message));
            throw;
        }

        CompleteTurn(session, fullText);
        // RunDone 不在此处发射——RunStreamingCoreAsync 在流正常跑完时已统一发射（对所有消费者
        // 一致：聊天/工作流/Automation），此处再发会双发。
        return fullText;
    }

    /// <summary>
    /// 用一个**已构建好的** <see cref="AIAgent"/>（如 workflow 包成的 agent）流式运行一轮，
    /// 复用与 <see cref="RunStreamingAsync(Guid,string,StreamHandlers?,ToolApprovalDecision?,string?,CancellationToken)"/>
    /// 相同的流式循环 + 事件提取。无 AetherSession 簿记（调用方自管会话）。
    /// 供 ICustomWorkflowRunner 等把组合 agent 当单 agent 跑的场景使用。
    /// </summary>
    public async Task<string> RunStreamingAsync(
        AIAgent agent,
        FrameworkAgentSession frameworkSession,
        string userMessage,
        StreamHandlers? handlers = null,
        ToolApprovalDecision? approvalResponse = null,
        string? desiredMode = null,
        CancellationToken cancellationToken = default)
    {
        if (desiredMode is not null)
            await TrySetModeAsync(agent, frameworkSession, desiredMode, cancellationToken).ConfigureAwait(false);
        try
        {
            // RunDone 由 RunStreamingCoreAsync 在流正常跑完时发射（两重载共用，见该处注释）。
            return await RunStreamingCoreAsync(agent, frameworkSession, userMessage, handlers, approvalResponse, cancellationToken,
                onToolCallName: null, _progressSink);
        }
        catch (Exception ex)
        {
            // 同 AetherSession 重载：异常收敛也要让进度消费者收到 RunError（无簿记场景亦如此）。
            _progressSink?.Post(new RunProgress(RunProgressKind.RunError, Text: ex.Message));
            throw;
        }
    }

    /// <summary>读取当前会话的 AgentMode（plan/execute…）；agent 未启用 AgentModeProvider 返回 null。</summary>
    public async Task<string?> GetModeAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var session = _sessions.Get(sessionId);
        if (session is null) return null;
        var agent = _registry.GetOrCreateAgent(_registry.Resolve(session.AgentId));
        var frameworkSession = await _registry.GetOrCreateFrameworkSessionAsync(sessionId, agent, cancellationToken);
        return await TryGetModeAsync(agent, frameworkSession, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取已构建 agent 的当前 AgentMode（供 workflow 路径复用）。</summary>
    public static async Task<string?> TryGetModeAsync(AIAgent agent, FrameworkAgentSession frameworkSession, CancellationToken cancellationToken = default)
    {
        var provider = agent.GetService<AgentModeProvider>();
        if (provider is null) return null;
        return await provider.GetModeAsync(frameworkSession, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>设置当前会话的 AgentMode；agent 未启用 AgentModeProvider 返回 false。</summary>
    public async Task<bool> SetModeAsync(Guid sessionId, string mode, CancellationToken cancellationToken = default)
    {
        var session = _sessions.Get(sessionId);
        if (session is null) return false;
        var agent = _registry.GetOrCreateAgent(_registry.Resolve(session.AgentId));
        var frameworkSession = await _registry.GetOrCreateFrameworkSessionAsync(sessionId, agent, cancellationToken);
        return await TrySetModeAsync(agent, frameworkSession, mode, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>设置已构建 agent 的 AgentMode（供 workflow 路径复用）。</summary>
    public static async Task<bool> TrySetModeAsync(AIAgent agent, FrameworkAgentSession frameworkSession, string mode, CancellationToken cancellationToken = default)
    {
        var provider = agent.GetService<AgentModeProvider>();
        if (provider is null) return false;
        await provider.SetModeAsync(frameworkSession, mode, cancellationToken).ConfigureAwait(false);
        return true;
    }


    /// <summary>
    /// 流式运行核心：围一轮 run 起 OTel Activity + ambient 执行上下文，选择调用重载（首轮用户文本 vs
    /// <see cref="ApprovalRecovery"/> 恢复流），把流交给 <see cref="AgentStreamDispatcher"/> 按
    /// <see cref="StreamHandlers"/> 上抛答案 token / 推理 / 工具事件 / 审批请求 / 待办 / 错误 / 用量。
    /// <paramref name="onToolCallName"/> 供带 AetherSession 的调用方记录工具名（可选）。
    /// <para>终态 RunDone/RunError 归属统一在此（两个 RunStreamingAsync 重载共用）：流正常跑完发
    /// RunDone；异常收敛由重载层兜底发 RunError（带簿记的层同时落 Faulted）。</para>
    /// </summary>
    private static async Task<string> RunStreamingCoreAsync(
        AIAgent agent, FrameworkAgentSession frameworkSession, string userMessage,
        StreamHandlers? handlers, ToolApprovalDecision? approvalResponse,
        CancellationToken cancellationToken,
        Action<string>? onToolCallName,
        IRunProgressSink? progressSink = null,
        bool emitTelemetry = false, string? agentId = null, string? conversationId = null, string? mode = null)
    {
        // 每轮 run 把「当前 agent / conversation」写入 ambient 上下文（AsyncLocal），供宿主层工具
        // （agent_add_tools / agent_reload 等）精准定位是谁在调自己。保存旧值 + finally 恢复，
        // 保证并发/嵌套 run 不串扰（AsyncLocal 按执行流隔离，但显式恢复覆盖异常路径）。
        var prevAgentId = AgentExecutionContext.CurrentAgentId.Value;
        var prevConversationId = AgentExecutionContext.CurrentConversationId.Value;
        AgentExecutionContext.CurrentAgentId.Value = agentId;
        AgentExecutionContext.CurrentConversationId.Value = conversationId;
        try
        {
        // OpenTelemetry：EnableOpenTelemetry 时围本轮 run 起 Activity（无 listener 时 StartActivity 返回 null，cheap）。
        using var activity = emitTelemetry
            ? Aether.AI.Harness.Telemetry.AetherActivitySource.Instance.StartActivity(Aether.AI.Harness.Telemetry.AetherActivitySource.ActivityAgentRun)
            : null;
        if (activity is not null)
        {
            if (agentId is not null) activity.SetTag(Aether.AI.Harness.Telemetry.AetherActivitySource.TagAgentId, agentId);
            if (conversationId is not null) activity.SetTag(Aether.AI.Harness.Telemetry.AetherActivitySource.TagConversationId, conversationId);
            if (mode is not null) activity.SetTag(Aether.AI.Harness.Telemetry.AetherActivitySource.TagMode, mode);
        }

        // 选择调用重载：审批恢复轮带响应消息（取回暂存/构造响应/两道守卫集中在 ApprovalRecovery，
        // 短路时终态信号已在其内部发好）；首轮带用户文本。
        IAsyncEnumerable<AgentResponseUpdate> stream;
        if (approvalResponse is not null)
        {
            var recovery = await ApprovalRecovery.TryBeginRecoveryAsync(
                agent, frameworkSession, approvalResponse, handlers, progressSink, cancellationToken).ConfigureAwait(false);
            if (recovery is null) return string.Empty;
            stream = recovery;
        }
        else
        {
            stream = agent.RunStreamingAsync(userMessage, frameworkSession, cancellationToken: cancellationToken);
        }

        var dispatcher = new AgentStreamDispatcher(agent, frameworkSession, handlers, onToolCallName, activity, progressSink);
        await dispatcher.DispatchAsync(stream, cancellationToken).ConfigureAwait(false);

        if (activity is not null) activity.SetTag(Aether.AI.Harness.Telemetry.AetherActivitySource.TagStatus, "ok");
        // 关键节点：本轮流正常跑完 → RunDone。**终态归属统一在此**（两个 RunStreamingAsync 重载都经由本方法，
        // 故聊天/工作流/Automation 三条路径、以及带簿记与不带簿记两种调用者拿到完全一致的终态；重载层不再发射
        // 以免双发）。审批打断是**本轮的正常结束**（工具待确认，非出错），故亦发 RunDone——消费者落到 Idle，
        // 恢复轮跑完再发一次 RunDone，桌宠可再冒一次「搞定啦」，不必为审批单独造信号。
        progressSink?.Post(new RunProgress(RunProgressKind.RunDone));
        return dispatcher.Text;
        }
        finally
        {
            AgentExecutionContext.CurrentAgentId.Value = prevAgentId;
            AgentExecutionContext.CurrentConversationId.Value = prevConversationId;
        }
    }

    /// <summary>
    /// 注册工作流会话→conversationId 映射，供 <c>RestoringChatHistoryProvider</c> 在重启续聊时
    /// 按 conversationId 加载 transcript 重建 LLM 历史。工作流路径（<c>RunStreamingAsync(AIAgent,…)</c>
    /// 重载）自管 <c>FrameworkAgentSession</c>、不经 <see cref="AgentRuntimeRegistry.GetOrCreateFrameworkSessionAsync"/>，
    /// 故需显式注册——否则 RestoringChatHistoryProvider 对工作流会话失效（LLM 上下文空，只 UI 显示有历史）。
    /// </summary>
    public void RegisterWorkflowSession(FrameworkAgentSession session, Guid conversationId)
        => _registry.RegisterWorkflowSession(session, conversationId);

    /// <summary>
    /// 会话删除时清出运行时会话状态：丢弃注册表（<see cref="AgentRuntimeRegistry"/>）里的 FrameworkAgentSession
    /// （agent 路径）+ <see cref="SessionManager"/> 里的 AgentSession。一旦这两个强引用切断，session 对象即
    /// 可回收——RestoringChatHistoryProvider 的 per-session 缓存（ConditionalWeakTable 弱键）随之随 GC 自动清，
    /// 无需 provider↔runtime 清理布线。工作流路径的 session 不在此处（自管于 CustomWorkflowRunner._sessions），
    /// 由 <c>ICustomWorkflowRunner.EvictConversation</c> 直接清。会话删除前 UI 已 <c>CancelRunningRun</c>，故无在飞 run 持有该 session。
    /// </summary>
    public void EvictSession(Guid sessionId)
    {
        _registry.DropFrameworkSession(sessionId);
        _sessions.Remove(sessionId);
        // SessionConversationMap 是 ConditionalWeakTable 弱键，session 回收后自动清，无需显式删。
    }

    /// <summary>
    /// 重建某会话的 frameworkSession（丢弃绑旧 agent 实例/旧 tools 的 MAF session），但**保留** SessionManager 里的
    /// AetherSession（sessionId 继续有效）。下次 <see cref="RunStreamingAsync(Guid,string,StreamHandlers?,ToolApprovalDecision?,string?,CancellationToken)"/>
    /// 再取框架 session 时因注册表已无该 sessionId 而重建——
    /// <see cref="RestoringChatHistoryProvider"/> 会按 conversationId 从 DB（<see cref="IConversationStore"/>）恢复历史，
    /// 故重建不丢多轮记忆。用途：编辑 agent（改 tools/systemPrompt 等）后让新定义下次对话生效，但不重置会话。
    /// </summary>
    public void RebuildSessionFramework(Guid sessionId)
        => _registry.DropFrameworkSession(sessionId);

    private static IEnumerable<string> ExtractToolCalls(AgentResponse response)
    {
        var names = new List<string>();
        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent call && !string.IsNullOrEmpty(call.Name))
                    names.Add(call.Name);
            }
        }
        return names;
    }
}
