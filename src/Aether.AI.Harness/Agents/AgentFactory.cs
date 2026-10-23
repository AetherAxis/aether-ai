using Aether.AI.Brains.Abstractions;
using Aether.AI.Core.Agents;
using Aether.AI.Harness.Skills;
using Aether.AI.Harness.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// Builds a runnable Microsoft Agent Framework <see cref="AIAgent"/> from an
/// <see cref="AgentDefinition"/>. By default the agent is a
/// <see cref="HarnessAgent"/> — the batteries-included agent from
/// <c>Microsoft.Agents.AI.Harness</c> — with the heavyweight providers
/// (web search, todos, agent-mode, file memory, OpenTelemetry) **opt-in per
/// definition** (all default off → lightweight runtime; the definition's
/// <c>Enable*</c> flags switch them on). Skills are on-demand for every agent
/// when <see cref="SkillsDirectory"/> is configured (MAF
/// <c>AgentFileSkillsSource</c>, script-capable); without it, agents fall back
/// to the <see cref="SkillRegistry"/> prompt-only set.
/// <para>
/// The brain is resolved through <see cref="IBrainFactoryRegistry"/>: LLM, rule and code brains
/// all enter through <see cref="IBrainFactory.CreateChatClient"/> and drive the same
/// tool/streaming/approval ecosystem. <see cref="BrainCapabilities.NaturalLanguageContext"/>
/// decides whether the natural-language providers and sampling options are wired.
/// </para>
/// <para>
/// Set <see cref="UseChatClientAgent"/> to use the lighter
/// <see cref="ChatClientAgent"/> instead, which skips the Harness pipeline
/// entirely.
/// </para>
/// <para>
/// The agent's <see cref="AgentDefinition.ModelId"/> is resolved to an
/// <see cref="IChatClient"/> via <see cref="Aether.AI.Brains.Abstractions.IChatClientFactory"/>, and its
/// <see cref="AgentDefinition.Memory"/> policy is enforced by configuring the
/// framework's chat-history provider at build time. Non-Safe tools are wrapped
/// (<c>ApprovalRequiredAIFunction</c>) when the definition opts into approval
/// or the mode selector, so the 4-mode gating can intercept them.
/// </para>
/// <para>
/// Three optional <c>Func&lt;AgentDefinition,…&gt;</c> ctor hooks keep this class
/// decoupled from its consumers: <c>memoryContextProviderFactory</c> (long-term
/// memory recall/store), <c>historyProviderFactory</c> (custom chat history,
/// e.g. restore-from-store), <c>backgroundAgentsFactory</c> (dynamic
/// multi-agent parallelism whitelist → <c>BackgroundAgentsProvider</c>).
/// </para>
/// </summary>
public sealed class AgentFactory
{
    private readonly Aether.AI.Brains.Abstractions.IChatClientFactory? _chatClientFactory;
    private readonly ToolRegistry _toolRegistry;
    private readonly SkillRegistry _skillRegistry;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly SkillScriptRunner _scriptRunner;
    private readonly AgentFileSkillScriptRunner _skillScriptRunner;
    private readonly Func<AgentDefinition, AIContextProvider?>? _memoryContextProviderFactory;
    private readonly Func<AgentDefinition, ChatHistoryProvider?>? _historyProviderFactory;
    private readonly Func<AgentDefinition, IReadOnlyList<AIAgent>?>? _backgroundAgentsFactory;
    private readonly IBrainFactoryRegistry? _brainFactories;
    private readonly IBrainRoutingClientFactory? _routingFactory;
    private readonly List<WeakReference<ISessionStateOwner>> _sessionStateOwners = new();
    private readonly object _sessionStateGate = new();

    /// <summary>
    /// When <c>true</c> (default), builds a <see cref="HarnessAgent"/>. When
    /// <c>false</c>, builds a plain <see cref="ChatClientAgent"/>.
    /// </summary>
    public bool UseChatClientAgent { get; set; } = false;

    /// <summary>
    /// HarnessAgent 文件记忆 provider 的可写根目录（每 agent 在其下建子目录）。
    /// 为空时启用文件记忆的 agent 会被静默禁用该 provider，避免默认相对路径在只读 CWD 下崩溃。
    /// 由宿主通过 AetherAIOptions.FileMemoryRoot 设置。
    /// </summary>
    public string? FileMemoryRoot { get; set; }

    /// <summary>skills 目录。配置后用 MAF AgentFileSkillsSource 加载（支持脚本）；否则退回 SkillRegistry 纯提示词。</summary>
    public string? SkillsDirectory { get; set; }

    /// <summary>
    /// 执行 .py 脚本的解释器（默认 python3）。由宿主通过 AetherAIOptions.PythonExecutable 在构造后设置，
    /// 转发给 <see cref="SkillScriptRunner"/>（实际执行件）。
    /// </summary>
    public string? PythonExecutable
    {
        get => _scriptRunner.PythonExecutable;
        set => _scriptRunner.PythonExecutable = value;
    }

    /// <param name="chatClientFactory">
    /// 兼容路径：宿主只提供模型客户端、未注册 LLM 大脑工厂时使用。可空——纯 Rule/Code 宿主
    /// 不需要模型工厂。经 <c>AddLlmBrains()</c> 注册后，LLM 与其他大脑一样走统一注册表。
    /// </param>
    public AgentFactory(Aether.AI.Brains.Abstractions.IChatClientFactory? chatClientFactory, ToolRegistry toolRegistry, SkillRegistry skillRegistry, ILoggerFactory? loggerFactory = null,
        Func<AgentDefinition, AIContextProvider?>? memoryContextProviderFactory = null,
        Func<AgentDefinition, ChatHistoryProvider?>? historyProviderFactory = null,
        Func<AgentDefinition, IReadOnlyList<AIAgent>?>? backgroundAgentsFactory = null,
        IBrainFactoryRegistry? brainFactories = null,
        IBrainRoutingClientFactory? routingFactory = null)
    {
        _chatClientFactory = chatClientFactory;
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _skillRegistry = skillRegistry ?? throw new ArgumentNullException(nameof(skillRegistry));
        _loggerFactory = loggerFactory;
        // skill 脚本子进程执行件（原内嵌于本类约 230 行，拆为 SkillScriptRunner；经 MAF 委托注入 AgentFileSkillsSource）。
        _scriptRunner = new SkillScriptRunner(loggerFactory);
        _skillScriptRunner = _scriptRunner.RunAsync;
        _memoryContextProviderFactory = memoryContextProviderFactory;
        _historyProviderFactory = historyProviderFactory;
        _backgroundAgentsFactory = backgroundAgentsFactory;
        _brainFactories = brainFactories;
        _routingFactory = routingFactory;
    }

    /// <summary>Builds a cached, runnable agent for the given definition.</summary>
    public AIAgent Create(AgentDefinition definition)
    {
        if (definition is null) throw new ArgumentNullException(nameof(definition));

        // AIFunction derives from AITool, but List<AIFunction> is not assignable
        // to IList<AITool>; widen to IList<AITool> for the framework APIs.
        // ask_user（交互提问工具）若已注册则自动纳入各 agent，免逐 agent 配置；agent 仍需 prompt 指示才会调用。
        // MCP 工具由宿主在启动后注册进 ToolRegistry（与原生工具同源），专家在工具选择器勾选 → definition.Tools，
        // 这里一并 ResolveAll 解析——MCP 工具走与原生工具完全相同的路径（含审批包装）。
        var toolNames = definition.Tools.ToList();
        if (!toolNames.Contains("ask_user") && _toolRegistry.Contains("ask_user"))
            toolNames.Add("ask_user");
        IList<AITool> tools = _toolRegistry.ResolveAll(toolNames).Cast<AITool>().ToList();

        // 按专家开关把"危险"工具包成 ApprovalRequiredAIFunction：模型调用时流冒出
        // ToolApprovalRequestContent，由 UI 弹审批框（协作式审批）。等级优先取专家的 ToolApprovals 覆盖，
        // 否则取 ToolRegistry 声明等级——Safe 自动放行，Confirm/Dangerous 才包装。
        // MCP 工具注册时默认标 Dangerous（来源未知），故 RequireToolApproval 时也会被包装。
        // EnableAgentModeProvider 也触发包装：4 档模式选择器（Manual/AutoEdit/Auto）靠审批请求逐工具拦截，
        // 不包装则非 Safe 工具直接自动跑、档位失效。Safe 工具（文件编辑/读/ask_user）不包装，始终内联自动跑。
        if (definition.RequireToolApproval || definition.EnableAgentModeProvider)
        {
            tools = tools
                .Select(t => t is AIFunction fn && ResolveApprovalLevel(fn.Name, definition) != ToolApprovalLevel.Safe
                    ? new ApprovalRequiredAIFunction(fn) : t)
                .ToList();
        }

        var historyProvider = _historyProviderFactory?.Invoke(definition) ?? BuildHistoryProvider(definition);

        // 大脑选择不再按 EngineKind 硬编码：所有大脑（含 LLM）经 IBrainFactory 注册表解析，
        // 能力集决定 Harness 怎么接线。未注册任何工厂时退回宿主的 IChatClientFactory（LLM 兼容路径）。
        var capabilities = _brainFactories?.CapabilitiesFor(definition)
            ?? BrainCapabilitiesDefaults.For(definition.Engine);
        AgentDefinitionValidation.ThrowIfInvalid(definition, capabilities);
        LogDefinitionDiagnostics(definition, capabilities);

        if (definition.EnableBrainRouting)
        {
            if (_routingFactory is null)
                throw new InvalidOperationException("Brain routing is enabled but no routing implementation is registered.");
            var routingClient = _routingFactory.Create(definition);
            TrackSessionStateOwner(routingClient);
            // 路由 agent 固定走确定性接线：Rule 分支需要「本轮最后一条业务 user 消息」精确，
            // TodoProvider 等注入式 NL 上下文会插入第二条 user 消息污染 LatestUser。
            // 代价是混合分支的 LLM 侧不享受 skills/todo/agent-mode（见 doc/brain-seam.md 已知边界）。
            return BuildBrainAgent(definition, tools, historyProvider, routingClient);
        }

        if (_brainFactories is not null && _brainFactories.TryResolve(definition, out var brainFactory)
            && brainFactory is not null)
        {
            // 接缝是 IChatClient：确定性大脑（规则/代码）由 BrainChatClient 把 IBrain 适配成传输，
            // 工具执行/回填/循环仍归 MAF HarnessAgent（与 LLM 路径同源）；LLM 工厂直接给模型客户端。
            var client = brainFactory.CreateChatClient(definition);
            TrackSessionStateOwner(client);
            return capabilities.HasFlag(BrainCapabilities.NaturalLanguageContext)
                ? (UseChatClientAgent
                    ? BuildChatClientAgent(definition, tools, historyProvider, client)
                    : BuildHarnessAgent(definition, tools, historyProvider, client, capabilities))
                : BuildBrainAgent(definition, tools, historyProvider, client);
        }

        if (definition.Engine != EngineKind.Llm)
        {
            var backend = string.IsNullOrWhiteSpace(definition.BrainBackend)
                ? string.Empty
                : $" backend '{definition.BrainBackend}'";
            throw new InvalidOperationException(
                $"No {definition.Engine}{backend} brain implementation is registered for agent '{definition.Id}'. " +
                "Register the corresponding brain package explicitly (AddRuleBrains / AddCSharpCodeBrains).");
        }

        if (_chatClientFactory is null)
        {
            throw new InvalidOperationException(
                $"No LLM brain is registered for agent '{definition.Id}'. " +
                "Call AddLlmBrains() or register an IChatClientFactory.");
        }

        var fallbackClient = _chatClientFactory.Create(definition.ModelId);
        return UseChatClientAgent
            ? BuildChatClientAgent(definition, tools, historyProvider, fallbackClient)
            : BuildHarnessAgent(definition, tools, historyProvider, fallbackClient, capabilities);
    }

    /// <summary>
    /// 记录「配置了但当前大脑不会消费」的告警。构建每 agent 只发生一次（实例被运行时缓存），
    /// 故不会逐轮刷屏——这正是把静默忽略显式化的成本最低点。
    /// </summary>
    private void LogDefinitionDiagnostics(AgentDefinition definition, BrainCapabilities capabilities)
    {
        var logger = _loggerFactory?.CreateLogger<AgentFactory>();
        if (logger is null) return;

        foreach (var issue in AgentDefinitionValidation.Check(definition, capabilities))
        {
            if (!issue.IsError)
                logger.LogWarning("Agent '{AgentId}' definition [{Code}]: {Message}", definition.Id, issue.Code, issue.Message);
        }
    }

    internal void EvictSessionState(string sessionId)
    {
        lock (_sessionStateGate)
        {
            for (var i = _sessionStateOwners.Count - 1; i >= 0; i--)
            {
                if (!_sessionStateOwners[i].TryGetTarget(out var owner))
                {
                    _sessionStateOwners.RemoveAt(i);
                    continue;
                }
                owner.EvictSession(sessionId);
            }
        }
    }

    private void TrackSessionStateOwner(object candidate)
    {
        if (candidate is not ISessionStateOwner owner) return;
        lock (_sessionStateGate)
        {
            _sessionStateOwners.RemoveAll(reference => !reference.TryGetTarget(out _));
            _sessionStateOwners.Add(new WeakReference<ISessionStateOwner>(owner));
        }
    }

    /// <summary>
    /// 构建无自然语言能力的大脑 agent（规则、代码、路由组合）：用通用 IChatClient 适配器接入 HarnessAgent。
    /// 关掉所有注入式 NL 上下文 provider——它们不消费，且 TodoProvider 会污染 LatestUser。
    /// 工具执行、结果回填、循环仍由 MAF 框架负责（与 LLM 路径同源）。
    /// </summary>
    private AIAgent BuildBrainAgent(
        AgentDefinition definition,
        IList<AITool> tools,
        ChatHistoryProvider? historyProvider,
        IChatClient client)
    {

        var options = new HarnessAgentOptions
        {
            Id = definition.Id,
            Name = definition.Name,
            Description = $"Aether.AI {definition.Engine} agent '{definition.Id}'.",
            // 规则不消费注入式 NL 上下文——全关。TodoProvider 默认开，会注入「### Current todo list」
            // user 消息污染 BrainChatClient 的 LatestUser 检测（1.0 实测）。
            DisableWebSearch = true,
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableFileMemory = true,
            DisableOpenTelemetry = true,
            DisableAgentSkillsProvider = true, // skill 指令需 LLM 解析；规则大脑不读
            ChatOptions = new ChatOptions { Instructions = definition.Instructions, Tools = tools },
            ToolApprovalAgentOptions = BuildToolApprovalOptions(definition), // 规则 agent 若挂审批工具同样按名放行
            // 规则大脑靠 history 重建轮内状态（最新 user 消息 + 本轮工具调用计数），必须保留轮内 history。
            // NoMemoryChatHistoryProvider（Memory=Off）连轮内 history 也清掉 → 工具调用后 MAF re-invoke 时
            // 只传 [tool:result]，user 消息丢失 → LatestUser=null → 误报「无匹配规则」。故规则 agent 遇
            // NoMemory 退回框架默认（InMemory，保留轮内 history）；规则大脑只读「最后一条 user + 其后工具调用」，
            // 跨轮累积的历史被忽略，无害。SlidingWindow/Full 本就保留轮内 history，原样放行。
            ChatHistoryProvider = historyProvider is NoMemoryChatHistoryProvider ? null : historyProvider,
        };

        return client.AsHarnessAgent(options, _loggerFactory);
    }

    private AIAgent BuildHarnessAgent(
        AgentDefinition definition,
        IList<AITool> tools,
        ChatHistoryProvider? historyProvider,
        IChatClient client,
        BrainCapabilities capabilities)
    {
        // 文件记忆仅在配置了可写根目录时才真正启用；否则静默禁用，避免默认相对路径在只读 CWD 下崩溃。
        var fileMemoryEnabled = definition.EnableFileMemory && !string.IsNullOrEmpty(FileMemoryRoot);

        var options = new HarnessAgentOptions
        {
            Id = definition.Id,
            Name = definition.Name,
            Description = $"Aether.AI agent '{definition.Id}'.",
            // Provider 开关由 AgentDefinition 的 Enable* 字段驱动（默认全 false = 全部禁用），
            // 保持轻量运行时；需要 web search / file memory / OTel 等能力的
            // agent 在定义里把对应 Enable* 置 true 即可。(Compaction 在未设 token 上限时自动禁用，无需开关。)
            DisableWebSearch = !definition.EnableWebSearch,
            DisableTodoProvider = !definition.EnableTodoProvider,
            DisableAgentModeProvider = !definition.EnableAgentModeProvider,
            DisableFileMemory = !fileMemoryEnabled,
            DisableOpenTelemetry = !definition.EnableOpenTelemetry,
            // 模式：MAF 1.22 自定义模式（4 档 Plan/Manual/AutoEdit/Auto 升为真 AgentMode——模型感知档位、
            // 每档独立指令；取代旧「MAF 只存 plan/execute + VM 层凑子档」的绕行）。目录见 AetherAgentModeCatalog，
            // 名字单一来源在 Core AgentModes（Mvvm/手机/测试同名引用）。mode_get/mode_set 工具原生禁用
            //（LLM 看不到但状态管理 + 指令注入保持；切档由宿主 SetModeAsync 驱动）。审批门控仍在宿主 VM 层
            // 逐工具硬门控，不依赖指令。
            AgentModeProviderOptions = definition.EnableAgentModeProvider
                ? new AgentModeProviderOptions
                {
                    DisableModeGetTool = true,
                    DisableModeSetTool = true,
                    Modes = AetherAgentModeCatalog.Modes,
                }
                : null,
            ChatOptions = BuildChatOptions(definition, tools, capabilities),
            ChatHistoryProvider = historyProvider,
            ToolApprovalAgentOptions = BuildToolApprovalOptions(definition),
            FileMemoryStore = fileMemoryEnabled
                ? new FileSystemAgentFileStore(Path.Combine(FileMemoryRoot!, definition.Id))
                : null,
            // skills provider 自建注入（见 BuildSkillsProvider）：聊天页无审批 UI，
            // 必须关掉 load/read/run 审批，否则 run_skill_script 被默认审批挡住、模型只 load 不执行。
            // 内部同名 provider 一并关掉，避免双重。
            DisableAgentSkillsProvider = true,
            // 长期记忆 / 后台 agent / skills 三条 NL 上下文 provider 由 BuildContextProviders 统一组装，
            // 与 ChatClientAgent 路径共用——否则 UseChatClientAgent 分支会静默丢配置。
            AIContextProviders = BuildContextProviders(definition),
        };

        return client.AsHarnessAgent(options, _loggerFactory);
    }

    /// <summary>
    /// 组装自然语言上下文 provider：skills / 长期记忆 / 后台 agent。
    /// <para>两条 LLM 路径（<see cref="HarnessAgent"/> 与 <see cref="ChatClientAgent"/>）共用同一份。
    /// 此前只有 Harness 路径接线，<c>UseChatClientAgent=true</c> 时长期记忆、skills、后台 agent
    /// 全部静默失效。二者都认 <c>AIContextProviders</c>，没有理由分叉。</para>
    /// <para>无 provider 时返回 <c>null</c>（不回写空列表）。</para>
    /// </summary>
    private List<AIContextProvider>? BuildContextProviders(AgentDefinition definition)
    {
        var providers = new List<AIContextProvider>();

        if (BuildSkillsProvider() is { } skills)
            providers.Add(skills);

        // 长期记忆 provider：EnableLongTermMemory 时由宿主工厂构造 MemoryContextProvider 注入。
        if (_memoryContextProviderFactory?.Invoke(definition) is { } memoryProvider)
            providers.Add(memoryProvider);

        // 后台 Agent（orchestrator 分派子任务）：BackgroundAgents 非空时由宿主工厂把各名字
        // 解析成 AIAgent 构建 BackgroundAgentsProvider 注入——让本 agent 动态并发委派子任务并收结果。
        // NOTE: BackgroundAgentsProvider 是 MAF 的 AIAgent（非 IChatClient）级 AIContextProvider，
        // 其 background_agents_* 工具在运行时由 provider 注册，无需手动挂进 ToolRegistry。
        if (definition.BackgroundAgents is { Count: > 0 } && _backgroundAgentsFactory is not null)
        {
            var subAgents = _backgroundAgentsFactory.Invoke(definition);
            if (subAgents is { Count: > 0 })
                providers.Add(new BackgroundAgentsProvider(subAgents));
        }

        return providers.Count == 0 ? null : providers;
    }

    /// <summary>按配置构建 skills provider；无技能可用时返回 <c>null</c>。</summary>
    private AgentSkillsProvider? BuildSkillsProvider()
    {
        // skill 全量按需加载（镜像 Claude Code / knowledge object 理念）：所有 skill 索引常驻
        // 每轮上下文，agent 运行时自己 load_skill 取正文（按需付费）。无 per-agent 白名单——
        // skill 是只读知识（load 只拉正文进上下文，agent 自己决定用不用），不需 ACL。
        //
        // MAF 1.22 起经 AgentSkillsProviderBuilder 构造：Build() 默认叠加
        // ①CachingAgentSkillsSource——索引扫描缓存（目录枚举 + SKILL.md front-matter 解析
        //   每 RefreshInterval 至多一次，并发串行化）；30s TTL 保「资料库改技能近实时生效」
        //   （旧裸构造每次 provider 拉索引都重扫目录）。
        // ②DeduplicatingAgentSkillsSource——同名 skill 去重防护（用户复制技能文件夹场景）。
        if (!string.IsNullOrEmpty(SkillsDirectory))
        {
            return new AgentSkillsProviderBuilder()
                .UseLoggerFactory(_loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance)
                .UseFileSkill(SkillsDirectory, options: null, _skillScriptRunner)
                .UseOptions(DisableSkillApprovals)
                .UseCachingOptions(c => c.RefreshInterval = TimeSpan.FromSeconds(30))
                .Build();
        }

        var skills = _skillRegistry.GetAll();
        if (skills.Count == 0) return null;

        var agentSkills = skills
            .Select(s => (AgentSkill)new AgentInlineSkill(
                new AgentSkillFrontmatter(s.Name, s.Description, ""), s.Content, null, null))
            .ToArray();
        // 内存源无 IO——builder 默认缓存恒命中零成本，无需 TTL 配置。
        return new AgentSkillsProviderBuilder()
            .UseLoggerFactory(_loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance)
            .UseSkills(agentSkills)
            .UseOptions(DisableSkillApprovals)
            .Build();
    }

    /// <summary>skill 工具审批全关（聊天页无审批 UI，MAF 默认审批会挡住 run_skill_script——模型只 load 不执行）。</summary>
    private static void DisableSkillApprovals(AgentSkillsProviderOptions o)
    {
        o.DisableLoadSkillApproval = true;
        o.DisableReadSkillResourceApproval = true;
        o.DisableRunSkillScriptApproval = true;
    }

    /// <summary>
    /// 轻量 <see cref="ChatClientAgent"/> 路径（<see cref="UseChatClientAgent"/>=true）：不叠 HarnessAgent
    /// 的中间件与注入式 provider，但**不意味着丢配置**——历史 provider、skills、长期记忆、后台 agent
    /// 与主路径共用同一套接线。只有 HarnessAgent 独有的开关（todo / agent-mode / 文件记忆 / web search /
    /// OTel / compaction）在此路径下无法生效，由 <see cref="WarnChatClientAgentFeatureGaps"/> 逐条告警。
    /// </summary>
    private AIAgent BuildChatClientAgent(
        AgentDefinition definition,
        IList<AITool> tools,
        ChatHistoryProvider? historyProvider,
        IChatClient client)
    {
        WarnChatClientAgentFeatureGaps(definition);

        var agentOptions = new ChatClientAgentOptions
        {
            Id = definition.Id,
            Name = definition.Name,
            Description = $"Aether.AI agent '{definition.Id}'.",
            ChatOptions = BuildChatOptions(definition, tools, BrainCapabilitiesDefaults.Llm),
            ChatHistoryProvider = historyProvider,
            AIContextProviders = BuildContextProviders(definition),
        };
        return new ChatClientAgent(client, agentOptions, _loggerFactory);
    }

    /// <summary>
    /// <c>UseChatClientAgent=true</c> 时逐条告警「这个定义要求的能力在该路径下不生效」——
    /// 与 <see cref="AgentDefinitionValidation"/> 同一原则：配置无效就报出来，不静默忽略。
    /// </summary>
    private void WarnChatClientAgentFeatureGaps(AgentDefinition definition)
    {
        var logger = _loggerFactory?.CreateLogger<AgentFactory>();
        if (logger is null) return;

        List<string>? gaps = null;
        if (definition.EnableTodoProvider) (gaps ??= new()).Add(nameof(definition.EnableTodoProvider));
        if (definition.EnableAgentModeProvider) (gaps ??= new()).Add(nameof(definition.EnableAgentModeProvider));
        if (definition.EnableFileMemory) (gaps ??= new()).Add(nameof(definition.EnableFileMemory));
        if (definition.EnableWebSearch) (gaps ??= new()).Add(nameof(definition.EnableWebSearch));
        if (definition.EnableOpenTelemetry) (gaps ??= new()).Add(nameof(definition.EnableOpenTelemetry));

        if (gaps is not null)
        {
            logger.LogWarning(
                "Agent '{AgentId}' uses UseChatClientAgent=true; HarnessAgent-only settings are ignored: {Settings}.",
                definition.Id, string.Join(", ", gaps));
        }
    }

    /// <summary>解析工具审批等级：专家 ToolApprovals 覆盖优先，否则取 ToolRegistry 声明等级。</summary>
    private ToolApprovalLevel ResolveApprovalLevel(string toolName, AgentDefinition definition)
    {
        if (definition.ToolApprovals is not null
            && definition.ToolApprovals.TryGetValue(toolName, out var overrideLevel)
            && Enum.TryParse<ToolApprovalLevel>(overrideLevel, ignoreCase: true, out var lvl))
        {
            return lvl;
        }
        return _toolRegistry.LevelOf(toolName);
    }

    /// <summary>
    /// 把 <see cref="AgentDefinition.AutoApprovalRules"/>（按工具名自动放行列表）转成 MAF
    /// <see cref="ToolApprovalAgentOptions"/>。规则在弹用户前评估：命中工具名→自动放行（true），
    /// 否则→继续评估下一条规则（false），最终落到 UI 弹框。与 <see cref="ApprovalRequiredAIFunction"/>
    /// 包装叠加：包装让非-Safe 工具发审批请求 → 中间件看请求 → 命中规则自动放行，否则弹框。
    /// <see cref="AgentDefinition.RequireToolApproval"/> 关时无包装→无审批请求→本规则惰性无副作用。
    /// </summary>
    private static ToolApprovalAgentOptions? BuildToolApprovalOptions(AgentDefinition definition)
    {
        if (definition.AutoApprovalRules is not { Count: > 0 } names) return null;
        var set = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new ToolApprovalAgentOptions
        {
            AutoApprovalRules = new Func<ToolAutoApprovalRuleContext, ValueTask<bool>>[]
            {
                ctx => new ValueTask<bool>(set.Contains(ctx.FunctionCallContent.Name ?? string.Empty)),
            },
        };
    }

    private static ChatHistoryProvider? BuildHistoryProvider(AgentDefinition definition)
        => definition.Memory switch
        {
            AgentMemoryMode.Off => new NoMemoryChatHistoryProvider(),
            AgentMemoryMode.SlidingWindow => new InMemoryChatHistoryProvider(
                new InMemoryChatHistoryProviderOptions
                {
                    ChatReducer = new SlidingWindowChatReducer(definition.MaxHistory),
                }),
            // Summarize 在此 static fallback 里退回滑动窗口——static 路径无 Aether.AI.Brains.Abstractions.IChatClientFactory，
            // 无法构造 SummarizingChatReducer。运行时由 Hosting 的 _historyProviderFactory（持 chatClientFactory）
            // 接真 SummarizingChatReducer；fallback 仅在未配工厂（如测试）时使用。
            AgentMemoryMode.Summarize => new InMemoryChatHistoryProvider(
                new InMemoryChatHistoryProviderOptions
                {
                    ChatReducer = new SlidingWindowChatReducer(definition.MaxHistory),
                }),
            _ => null, // Full = framework default
        };

    private static ChatOptions BuildChatOptions(
        AgentDefinition definition, IList<AITool> tools, BrainCapabilities capabilities)
    {
        var chatOptions = new ChatOptions
        {
            Instructions = definition.Instructions,
            Tools = tools,
        };

        // ask_user 可用时自动追加使用指示（否则 agent 默认纯文本提问，交互对话框形同虚设）。
        if (tools.Any(t => t is AIFunction f && string.Equals(f.Name, "ask_user", StringComparison.OrdinalIgnoreCase)))
        {
            chatOptions.Instructions = (string.IsNullOrEmpty(chatOptions.Instructions) ? "" : chatOptions.Instructions + "\n\n")
                + "当需要用户确认、拍板、或从多个选项中选择时，必须调用 ask_user 工具阻塞等待用户回答；"
                + "不要用纯文本提问后结束轮次等待用户输入。有多个需要确认的问题时，一次性传入 questions 数组（表单一次提交），"
                + "返回 JSON 数组按提问顺序给出每个的回答。"
                + "特别注意：当你打算用文本列出多个选项让用户选（如『○ 选项A / ○ 选项B / ○ 选项C』『你可以回复：…』『请指示👇』），"
                + "必须改为调用 ask_user，把这些选项作为单个 question 的 options 数组传入——绝不要用纯文本列选项后结束轮次。"
                + "在 execute（执行）模式下：开始执行计划前，先调用 ask_user 确认是否开始；"
                + "执行过程中遇到写、删、下单、运行脚本等不可逆或高风险操作前，也先调用 ask_user 确认，不要自主直接执行。"
                + "返回 [] 表示用户取消了提问（未回答），不要就同一问题重复调用 ask_user 追问——应停止当前操作、"
                + "向用户说明已取消并等待进一步指示，或改用其它方式继续；同一问题在一次会话中最多调用 ask_user 一次。";
        }

        // 采样/输出格式只对声明了 Sampling 能力的大脑生效（LLM）；由能力集而非引擎枚举决定。
        if (capabilities.HasFlag(BrainCapabilities.Sampling))
        {
            if (definition.Temperature is { } temperature)
            {
                chatOptions.Temperature = (float)temperature;
            }

            if (definition.TopP is { } topP)
            {
                chatOptions.TopP = (float)topP;
            }

            if (definition.MaxOutputTokens is { } maxTokens)
            {
                chatOptions.MaxOutputTokens = maxTokens;
            }

            if (definition.ResponseFormat != AgentResponseFormat.Default)
            {
                chatOptions.ResponseFormat = definition.ResponseFormat switch
                {
                    AgentResponseFormat.Text => ChatResponseFormat.Text,
                    AgentResponseFormat.Json => ChatResponseFormat.Json,
                    _ => null,
                };
            }
        }

        return chatOptions;
    }

}
