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
/// <see cref="AgentDefinition.Engine"/> == <see cref="EngineKind.Rule"/> swaps
/// the brain: <see cref="RuleChatClient"/> (deterministic rules, no LLM) drives
/// the same tool/streaming/approval ecosystem — see <c>BuildRuleAgent</c>.
/// </para>
/// <para>
/// Set <see cref="UseChatClientAgent"/> to use the lighter
/// <see cref="ChatClientAgent"/> instead, which skips the Harness pipeline
/// entirely.
/// </para>
/// <para>
/// The agent's <see cref="AgentDefinition.ModelId"/> is resolved to an
/// <see cref="IChatClient"/> via <see cref="IChatClientFactory"/>, and its
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
    private readonly IChatClientFactory _chatClientFactory;
    private readonly ToolRegistry _toolRegistry;
    private readonly SkillRegistry _skillRegistry;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly SkillScriptRunner _scriptRunner;
    private readonly AgentFileSkillScriptRunner _skillScriptRunner;
    private readonly Func<AgentDefinition, AIContextProvider?>? _memoryContextProviderFactory;
    private readonly Func<AgentDefinition, ChatHistoryProvider?>? _historyProviderFactory;
    private readonly Func<AgentDefinition, IReadOnlyList<AIAgent>?>? _backgroundAgentsFactory;
    private readonly ICodeBrainRegistry? _codeBrainRegistry;

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
    /// Resolves the <see cref="IChatClient"/> for a given agent model id.
    /// Invoked lazily per agent so the runtime does not eagerly build a client.
    /// </param>
    public AgentFactory(IChatClientFactory chatClientFactory, ToolRegistry toolRegistry, SkillRegistry skillRegistry, ILoggerFactory? loggerFactory = null,
        Func<AgentDefinition, AIContextProvider?>? memoryContextProviderFactory = null,
        Func<AgentDefinition, ChatHistoryProvider?>? historyProviderFactory = null,
        Func<AgentDefinition, IReadOnlyList<AIAgent>?>? backgroundAgentsFactory = null,
        ICodeBrainRegistry? codeBrainRegistry = null)
    {
        _chatClientFactory = chatClientFactory ?? throw new ArgumentNullException(nameof(chatClientFactory));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _skillRegistry = skillRegistry ?? throw new ArgumentNullException(nameof(skillRegistry));
        _loggerFactory = loggerFactory;
        // skill 脚本子进程执行件（原内嵌于本类约 230 行，拆为 SkillScriptRunner；经 MAF 委托注入 AgentFileSkillsSource）。
        _scriptRunner = new SkillScriptRunner(loggerFactory);
        _skillScriptRunner = _scriptRunner.RunAsync;
        _memoryContextProviderFactory = memoryContextProviderFactory;
        _historyProviderFactory = historyProviderFactory;
        _backgroundAgentsFactory = backgroundAgentsFactory;
        _codeBrainRegistry = codeBrainRegistry;
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

        // 规则大脑：确定性规则驱动工具调用，不委托 LLM。接缝是 IChatClient——RuleChatClient 产 AIContent，
        // 工具执行/回填/循环仍归 MAF HarnessAgent（与 LLM 路径同源）。规则不消费注入式 NL 上下文
        //（todo/mode/skill/memory-recall），BuildRuleAgent 内关掉这些 provider（1.0 已验证 TodoProvider
        // 会注入第二条 user 消息污染 LatestUser）。
        if (definition.Engine == EngineKind.Rule)
        {
            // 规则 agent 复用编辑器勾选的工具（definition.Tools + ask_user），与 LLM agent 一致——
            // 规则只调程序里点名的工具，但工具池由作者在编辑器显式选定。
            // 不经审批（规则定义 RequireToolApproval=false；规则是 Safe 确定性流程）。
            return BuildRuleAgent(definition, tools, historyProvider);
        }

        return UseChatClientAgent
            ? BuildChatClientAgent(definition, tools, historyProvider)
            : BuildHarnessAgent(definition, tools, historyProvider);
    }

    /// <summary>
    /// 构建规则大脑 agent：用 <see cref="RuleChatClient"/>（IChatClient）代替 LLM 客户端接入 HarnessAgent。
    /// 规则程序取自 <see cref="AgentDefinition.Rules"/>（已由适配器规整成 <see cref="RuleProgram"/> 对象）；
    /// null 退空程序（NoMatch 兜底）。关掉所有注入式 NL 上下文 provider——规则不消费它们，且 TodoProvider 会污染 LatestUser。
    /// 工具执行、结果回填、循环仍由 MAF 框架负责（与 LLM 路径同源）。
    /// </summary>
    private AIAgent BuildRuleAgent(AgentDefinition definition, IList<AITool> tools, ChatHistoryProvider? historyProvider)
    {
        // 代码规则大脑：按笔记 Id 从注册表取工厂得 fresh 实例（Roslyn 即时编译的 CodeRuleBrain）。
        // 未注册/编译失败 → ErrorReplyBrain 兜底，产一句提示终止循环。
        // 声明式 YAML 规则：RuleProgramBrain（definition.Rules）。
        IRuleBrain brain;
        if (definition.IsCodeRule)
        {
            // 未配 registry / 未注册 / 编译失败 → ErrorReplyBrain 兜底，产一句提示终止循环。
            // 预声明 + 预赋值，避免 TryCreate 短路未调时的「未赋值局部变量」编译错。
            IRuleBrain? codeBrain = null;
            string? codeError = null;
            bool got = _codeBrainRegistry is not null && _codeBrainRegistry.TryCreate(definition.Id, out codeBrain, out codeError);
            brain = got ? codeBrain! : new ErrorReplyBrain(codeError);
        }
        else
        {
            var program = definition.Rules ?? new RuleProgram();
            brain = new RuleProgramBrain(program);
        }
        var client = new RuleChatClient(brain, _loggerFactory?.CreateLogger<RuleChatClient>());

        var options = new HarnessAgentOptions
        {
            Id = definition.Id,
            Name = definition.Name,
            Description = $"Aether.AI rule agent '{definition.Id}'.",
            // 规则不消费注入式 NL 上下文——全关。TodoProvider 默认开，会注入「### Current todo list」
            // user 消息污染 RuleChatClient 的 LatestUser 检测（1.0 实测）。
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

    private AIAgent BuildHarnessAgent(AgentDefinition definition, IList<AITool> tools, ChatHistoryProvider? historyProvider)
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
            //（LLM 看不到但状态管理 + 指令注入保持；切档由宿主 SetModeAsync 驱动——取代旧 ToolFilteringChatClient
            // 包装，那版把工具名记反从未生效）。审批门控仍在宿主 VM 层逐工具硬门控，不依赖指令。
            AgentModeProviderOptions = definition.EnableAgentModeProvider
                ? new AgentModeProviderOptions
                {
                    DisableModeGetTool = true,
                    DisableModeSetTool = true,
                    Modes = AetherAgentModeCatalog.Modes,
                }
                : null,
            ChatOptions = BuildChatOptions(definition, tools),
            ChatHistoryProvider = historyProvider,
            ToolApprovalAgentOptions = BuildToolApprovalOptions(definition),
            FileMemoryStore = fileMemoryEnabled
                ? new FileSystemAgentFileStore(Path.Combine(FileMemoryRoot!, definition.Id))
                : null,
        };

        // skills provider 自建注入（见 ConfigureSkillsProvider）：聊天页无审批 UI，
        // 必须关掉 load/read/run 审批，否则 run_skill_script 被默认审批挡住、模型只 load 不执行。
        ConfigureSkillsProvider(options);

        // 长期记忆 provider：EnableLongTermMemory 时由宿主工厂构造 MemoryContextProvider 注入。
        // 与 skills provider 累加（ConfigureSkillsProvider 可能已设置 AIContextProviders）。
        var memoryProvider = _memoryContextProviderFactory?.Invoke(definition);
        if (memoryProvider is not null) AddContextProvider(options, memoryProvider);

        // 后台 Agent（orchestrator 分派子任务）：BackgroundAgents 非空时由宿主工厂把各名字
        // 解析成 AIAgent 构建 BackgroundAgentsProvider 注入——让本 agent 动态并发委派子任务并收结果。
        // NOTE: BackgroundAgentsProvider 是 MAF 的 AIAgent（非 IChatClient）级 AIContextProvider，
        // 其 background_agents_* 工具在 HarnessAgent 运行时由 provider 注册，无需手动挂进 ToolRegistry。
        if (definition.BackgroundAgents is { Count: > 0 } && _backgroundAgentsFactory is not null)
        {
            var subAgents = _backgroundAgentsFactory.Invoke(definition);
            if (subAgents is { Count: > 0 })
                AddContextProvider(options, new BackgroundAgentsProvider(subAgents));
        }

        var client = _chatClientFactory.Create(definition.ModelId);

        // mode_get/mode_set 工具的禁用已上移到 HarnessAgentOptions.AgentModeProviderOptions
        // （MAF 1.22 原生 DisableModeGetTool/SetTool——见上 options 构造处注释），
        // 不再需要 ToolFilteringChatClient 包装（旧版名字记反从未生效，见该类注释）。

        return client.AsHarnessAgent(options, _loggerFactory);
    }

    /// <summary>把 context provider 累加进 <c>options.AIContextProviders</c>（null 则新建；保序，可多次调用）。</summary>
    private static void AddContextProvider(HarnessAgentOptions options, AIContextProvider provider)
    {
        var combined = options.AIContextProviders is null
            ? new List<AIContextProvider>()
            : new List<AIContextProvider>(options.AIContextProviders);
        combined.Add(provider);
        options.AIContextProviders = combined;
    }

    private void ConfigureSkillsProvider(HarnessAgentOptions options)
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
        AgentSkillsProvider provider;
        if (!string.IsNullOrEmpty(SkillsDirectory))
        {
            provider = new AgentSkillsProviderBuilder()
                .UseLoggerFactory(_loggerFactory)
                .UseFileSkill(SkillsDirectory, options: null, _skillScriptRunner)
                .UseOptions(DisableSkillApprovals)
                .UseCachingOptions(c => c.RefreshInterval = TimeSpan.FromSeconds(30))
                .Build();
        }
        else
        {
            var skills = _skillRegistry.GetAll();
            if (skills.Count == 0) { options.DisableAgentSkillsProvider = true; return; }

            var agentSkills = skills
                .Select(s => (AgentSkill)new AgentInlineSkill(
                    new AgentSkillFrontmatter(s.Name, s.Description, ""), s.Content, null, null))
                .ToArray();
            // 内存源无 IO——builder 默认缓存恒命中零成本，无需 TTL 配置。
            provider = new AgentSkillsProviderBuilder()
                .UseLoggerFactory(_loggerFactory)
                .UseSkills(agentSkills)
                .UseOptions(DisableSkillApprovals)
                .Build();
        }

        options.DisableAgentSkillsProvider = true; // 关内部默认 provider，避免双重 + 避免其默认审批
        options.AIContextProviders = new List<AIContextProvider> { provider };
    }

    /// <summary>skill 工具审批全关（聊天页无审批 UI，MAF 默认审批会挡住 run_skill_script——模型只 load 不执行）。</summary>
    private static void DisableSkillApprovals(AgentSkillsProviderOptions o)
    {
        o.DisableLoadSkillApproval = true;
        o.DisableReadSkillResourceApproval = true;
        o.DisableRunSkillScriptApproval = true;
    }

    private AIAgent BuildChatClientAgent(AgentDefinition definition, IList<AITool> tools, ChatHistoryProvider? historyProvider)
    {
        var client = _chatClientFactory.Create(definition.ModelId);

        // When no custom history provider is needed (Full), use the lightweight
        // ctor which defaults to InMemoryChatHistoryProvider. Otherwise wire the
        // provider through ChatClientAgentOptions.
        if (historyProvider is null)
        {
            return new ChatClientAgent(
                client,
                instructions: definition.Instructions,
                name: definition.Name,
                description: $"Aether.AI agent '{definition.Id}'.",
                tools: tools,
                loggerFactory: _loggerFactory);
        }

        var agentOptions = new ChatClientAgentOptions
        {
            Name = definition.Name,
            Description = $"Aether.AI agent '{definition.Id}'.",
            ChatOptions = BuildChatOptions(definition, tools),
            ChatHistoryProvider = historyProvider,
        };
        return new ChatClientAgent(client, agentOptions, _loggerFactory);
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
            // Summarize 在此 static fallback 里退回滑动窗口——static 路径无 IChatClientFactory，
            // 无法构造 SummarizingChatReducer。运行时由 Hosting 的 _historyProviderFactory（持 chatClientFactory）
            // 接真 SummarizingChatReducer；fallback 仅在未配工厂（如测试）时使用。
            AgentMemoryMode.Summarize => new InMemoryChatHistoryProvider(
                new InMemoryChatHistoryProviderOptions
                {
                    ChatReducer = new SlidingWindowChatReducer(definition.MaxHistory),
                }),
            _ => null, // Full = framework default
        };

    private static ChatOptions BuildChatOptions(AgentDefinition definition, IList<AITool> tools)
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

        return chatOptions;
    }

}
