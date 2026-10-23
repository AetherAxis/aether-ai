using System.Collections.Concurrent;
using Aether.AI.Core.Agents;
using Aether.AI.Harness.Sessions;
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
    private readonly ConcurrentDictionary<Guid, string> _sessionStoreKeys = new();
    private readonly IAgentSessionStore? _sessionStore;

    internal AgentRuntimeRegistry(
        AgentFactory factory,
        SessionConversationMap sessionConversationMap,
        IAgentSessionStore? sessionStore = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _sessionConversationMap = sessionConversationMap ?? throw new ArgumentNullException(nameof(sessionConversationMap));
        _sessionStore = sessionStore;
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

        var storeKey = AgentSessionKeys.For(agent.Id, sessionId.ToString("N"));

        // 配了 IAgentSessionStore：先看有没有落盘的会话状态（跨进程续聊）。反序列化失败（旧格式、
        // 定义改过）不能拖垮启动——丢掉这条记录按新建处理，并把失败记录清掉免得每轮重试。
        FrameworkAgentSession? restored = null;
        if (_sessionStore is not null)
        {
            if (await _sessionStore.GetAsync(storeKey, cancellationToken).ConfigureAwait(false) is { } persisted)
            {
                try
                {
                    restored = await agent.DeserializeSessionAsync(persisted, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    await _sessionStore.RemoveAsync(storeKey, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        var created = restored ?? await agent.CreateSessionAsync(cancellationToken);
        // 原子落表：并发首跑（同 sessionId 双双 miss 上面的快查）时只留先到者，后到者弃用自己的
        // created、返回先到者——保证 map 里每 sessionId 恰一个 session（EvictSession/RebuildSessionFramework
        // 的 TryRemove 才不会漏删）。修复前是裸 `[sessionId] = created` 索引赋值，后写者覆盖先写者，
        // 先到的调用方继续持有已不在 map 里的孤儿 session——此前靠 CustomAgentRunner 的 per-conversation
        // 信号量在外层补偿，本方法自身不保证不变量。
        var winner = _frameworkSessions.GetOrAdd(sessionId, created);
        if (!ReferenceEquals(winner, created)) return winner;
        _sessionStoreKeys[sessionId] = storeKey;
        _sessionConversationMap.Register(created, sessionId); // 供 RestoringChatHistoryProvider 按 conversationId 加载 transcript

        // 新建的会话先落一次盘（恢复来的已在盘上，无需回写）。
        if (restored is null)
            await SaveFrameworkSessionAsync(sessionId, agent, created, cancellationToken).ConfigureAwait(false);

        return created;
    }

    /// <summary>
    /// 把当前会话状态写进 <see cref="IAgentSessionStore"/>（未配置存储时是 no-op）。
    /// <para>持久化失败**不**往外抛：会话还在内存里、本轮 run 也已完成，为一条落盘失败把用户的
    /// 回答判成错误本末倒置。失败即降级为「下次重启续不上」。</para>
    /// </summary>
    internal async Task SaveFrameworkSessionAsync(
        Guid sessionId, AIAgent agent, FrameworkAgentSession session, CancellationToken cancellationToken)
    {
        if (_sessionStore is null) return;

        var storeKey = AgentSessionKeys.For(agent.Id, sessionId.ToString("N"));
        try
        {
            var json = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await _sessionStore.SaveAsync(storeKey, json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 见上：落盘失败降级，不影响本轮结果。
        }
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
    {
        _frameworkSessions.TryRemove(sessionId, out _);
        // 落盘的那份也要删——否则「删除会话」下次启动会被存储里的旧状态复活。
        if (_sessionStoreKeys.TryRemove(sessionId, out var storeKey) && _sessionStore is { } store)
            _ = SafeRemoveAsync(store, storeKey);
        _factory.EvictSessionState(sessionId.ToString());
    }

    private static async Task SafeRemoveAsync(IAgentSessionStore store, string key)
    {
        try
        {
            await store.RemoveAsync(key).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 删除失败只影响「已删会话可能被恢复」，不该让 Delete 操作在 UI 层失败。
        }
    }
}
