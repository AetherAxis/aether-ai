using System.Collections.Concurrent;
using Aether.AI.Core.Agents;
using Microsoft.Agents.AI;

// Framework 与 Core 都有 AgentSession 同名类型；这里只用框架侧（模型聊天线程）。
using FrameworkAgentSession = Microsoft.Agents.AI.AgentSession;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// <see cref="HarnessRuntime"/> 的注册表/缓存层，三张表一体：agent 定义注册表（id→<see cref="AgentDefinition"/>）、
/// 已构建 agent 缓存（id→<see cref="AIAgent"/>）、per-conversation FrameworkAgentSession 表
/// （conversationId→框架会话线程）。
/// <para>生命周期操作全部落到这三张表上：<see cref="Invalidate"/>（编辑 agent→弃定义+实例缓存）、
/// <see cref="DropFrameworkSession"/>（弃框架线程——「保会话重建」与「会话删除」两场景共用；会话删除由
/// <c>HarnessRuntime</c> 侧再弃 <c>SessionManager</c> 里的 AetherSession，弱键缓存随 GC 自动清）。</para>
/// <para>从 <c>HarnessRuntime</c> 抽出——「定义解析 + agent 构建 + session 创建」是运行时状态职责，与执行
/// （流式核心/分发器）、簿记（turn 状态）分离。并发首跑竞态守卫（GetOrAdd + winner 检查，保证每
/// conversationId 恰一个框架 session）集中在 <see cref="GetOrCreateFrameworkSessionAsync"/> 一处。</para>
/// </summary>
internal sealed class AgentRuntimeRegistry
{
    private readonly AgentFactory _factory;
    private readonly SessionConversationMap _sessionConversationMap;
    private readonly ConcurrentDictionary<string, AgentDefinition> _definitions = new();
    private readonly ConcurrentDictionary<string, AIAgent> _agents = new();
    private readonly ConcurrentDictionary<Guid, FrameworkAgentSession> _frameworkSessions = new();

    internal AgentRuntimeRegistry(AgentFactory factory, SessionConversationMap sessionConversationMap)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _sessionConversationMap = sessionConversationMap ?? throw new ArgumentNullException(nameof(sessionConversationMap));
    }

    /// <summary>注册 agent 定义（同 id 覆盖），供按 id 启动会话。</summary>
    internal void Register(AgentDefinition definition)
    {
        if (definition is null) throw new ArgumentNullException(nameof(definition));
        _definitions[definition.Id] = definition;
    }

    /// <summary>是否已注册该 id 的 agent。</summary>
    internal bool Has(string agentId) => _definitions.ContainsKey(agentId);

    /// <summary>使已注册 agent 的缓存失效：移除其定义与已构建的 agent 实例，下次运行时重建。
    /// 用于自定义 Agent 被编辑后丢弃旧配置。会话线程由调用方（runner）丢弃并新建。</summary>
    internal void Invalidate(string agentId)
    {
        _definitions.TryRemove(agentId, out _);
        _agents.TryRemove(agentId, out _);
    }

    /// <summary>按 id 解析定义；未注册抛 <see cref="InvalidOperationException"/>（消息供 StartSession 等共享）。</summary>
    internal AgentDefinition Resolve(string agentId)
        => _definitions.TryGetValue(agentId, out var definition)
            ? definition
            : throw new InvalidOperationException($"No agent registered with id '{agentId}'.");

    /// <summary>取或建 agent（同 id 复用已构建实例；构建委托 <see cref="AgentFactory.Create"/>）。</summary>
    internal AIAgent GetOrCreateAgent(AgentDefinition definition)
        => _agents.GetOrAdd(definition.Id, _ => _factory.Create(definition));

    /// <summary>取或建某会话的框架 session（agent 路径的模型聊天线程）。</summary>
    internal async Task<FrameworkAgentSession> GetOrCreateFrameworkSessionAsync(
        Guid sessionId, AIAgent agent, CancellationToken cancellationToken)
    {
        if (_frameworkSessions.TryGetValue(sessionId, out var existing))
            return existing;

        var created = await agent.CreateSessionAsync(cancellationToken);
        // 原子落表：并发首跑（同 sessionId 双双 miss 上面的快查）时只留先到者，后到者弃用自己的
        // created、返回先到者——保证 map 里每 sessionId 恰一个 session（EvictSession/RebuildSessionFramework
        // 的 TryRemove 才不会漏删）。修复前是裸 `[sessionId] = created` 索引赋值，后写者覆盖先写者，
        // 先到的调用方继续持有已不在 map 里的孤儿 session——此前靠 CustomAgentRunner 的 per-conversation
        // 信号量在外层补偿，本方法自身不保证不变量。
        var winner = _frameworkSessions.GetOrAdd(sessionId, created);
        if (!ReferenceEquals(winner, created)) return winner;
        _sessionConversationMap.Register(created, sessionId); // 供 RestoringChatHistoryProvider 按 conversationId 加载 transcript
        return created;
    }

    /// <summary>
    /// 注册工作流会话→conversationId 映射，供 <c>RestoringChatHistoryProvider</c> 在重启续聊时
    /// 按 conversationId 加载 transcript 重建 LLM 历史。工作流路径（<c>RunStreamingAsync(AIAgent,…)</c>
    /// 重载）自管 <c>FrameworkAgentSession</c>、不经 <see cref="GetOrCreateFrameworkSessionAsync"/>，
    /// 故需显式注册——否则 RestoringChatHistoryProvider 对工作流会话失效（LLM 上下文空，只 UI 显示有历史）。
    /// </summary>
    internal void RegisterWorkflowSession(FrameworkAgentSession session, Guid conversationId)
        => _sessionConversationMap.Register(session, conversationId);

    /// <summary>丢弃某会话的框架 session 强引用（「保会话重建」与「会话删除」两场景共用）。
    /// 一旦强引用切断，session 即可回收——弱键缓存（SessionConversationMap /
    /// RestoringChatHistoryProvider 的 ConditionalWeakTable）随之随 GC 自动清。</summary>
    internal void DropFrameworkSession(Guid sessionId)
        => _frameworkSessions.TryRemove(sessionId, out _);
}
