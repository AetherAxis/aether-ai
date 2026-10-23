namespace Aether.AI.Core.Agents;

/// <summary>
/// Declarative description of an agent: its identity, instructions, the model
/// it should run on, and the names of tools it is allowed to use. This is a
/// plain data model — the Harness layer is responsible for turning an
/// <see cref="AgentDefinition"/> into a live, runnable agent.
/// </summary>
public sealed class AgentDefinition
{
    /// <summary>Stable logical identifier for the agent (e.g. "sales-assistant").</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Display name shown to users.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// 大脑类型：默认 <see cref="EngineKind.Llm"/>；另有声明式 <see cref="EngineKind.Rule"/>
    /// 与程序式 <see cref="EngineKind.Code"/>。三类均通过 Harness 复用工具、流式与会话设施。
    /// </summary>
    public EngineKind Engine { get; init; } = EngineKind.Llm;

    /// <summary>
    /// 同一 <see cref="Engine"/> 下的实现变体标识，例如规则后端的 <c>program</c>（内置声明式程序）、
    /// <c>nRules</c>、<c>rulesEngine</c>。<c>null</c>/空 = 使用该引擎唯一注册的实现；
    /// 宿主同时注册多个后端时必须显式填写——运行时不会按注册顺序猜。
    /// </summary>
    public string? BrainBackend { get; init; }

    /// <summary>
    /// 声明式规则程序。固定 Rule 大脑直接执行；显式 Routing 用它做无副作用匹配并在命中后执行。
    /// 适配器可从 YAML 规整成该对象，全程不经 JSON 中转。
    /// </summary>
    public RuleProgram? Rules { get; init; }

    /// <summary>
    /// 规则后端的原生定义（JSON/文本）。内置 <c>program</c> 后端读取 <see cref="Rules"/>；
    /// 其他后端（如 <c>rulesEngine</c>）读取此字段——适配器负责解析，Core 不理解其格式。
    /// </summary>
    public string? RuleSource { get; init; }

    /// <summary>
    /// 代码大脑语言。仅有效大脑类型为 <see cref="EngineKind.Code"/> 时使用；默认 C#，兼容旧代码笔记。
    /// 该字段不表示是否动态编译，已编译类型与源码编译使用同一语言标识。
    /// </summary>
    public CodeBrainLanguage CodeLanguage { get; init; } = CodeBrainLanguage.CSharp;

    /// <summary>
    /// 显式启用规则优先、LLM 兜底路由。默认关闭，不改变旧 Agent 的固定大脑语义。
    /// </summary>
    public bool EnableBrainRouting { get; init; }

    /// <summary>The system prompt / instructions that shape agent behavior.</summary>
    public string Instructions { get; init; } = string.Empty;

    /// <summary>The model id to use (e.g. "gpt-4o-mini"). Resolved by the Harness.</summary>
    public string ModelId { get; init; } = string.Empty;

    /// <summary>
    /// Optional temperature override. <c>null</c> means use the provider default.
    /// </summary>
    public double? Temperature { get; init; }

    /// <summary>
    /// Names of tools (registered with the ToolRegistry) the agent may invoke.
    /// </summary>
    public IReadOnlyList<string> Tools { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 按工具名覆盖审批等级（值："Safe"/"Confirm"/"Dangerous"）。未列入的工具取 ToolRegistry 声明等级。
    /// 供专家作者在编辑页逐工具调整危险等级（默认取声明等级，可覆盖）。
    /// </summary>
    public IReadOnlyDictionary<string, string>? ToolApprovals { get; init; }

    /// <summary>
    /// 按工具名自动放行的列表（MAF <c>ToolApprovalAgentOptions.AutoApprovalRules</c>）。当
    /// <see cref="RequireToolApproval"/> 开启时，列入此列表的非-Safe 工具调用由常驻规则自动放行、
    /// 不弹审批框；未列入的仍走 UI 审批。与 <see cref="ToolApprovals"/>（覆盖等级）正交：后者调危险等级，
    /// 前者决定放行与否。null/空=无自动放行规则。Safe 工具本就自动跑，列入无副作用。
    /// </summary>
    public IReadOnlyList<string>? AutoApprovalRules { get; init; }

    /// <summary>
    /// Conversation memory policy. Defaults to <see cref="AgentMemoryMode.Full"/>.
    /// Enforced by the Harness via the framework's chat-history provider.
    /// </summary>
    public AgentMemoryMode Memory { get; init; } = AgentMemoryMode.Full;

    /// <summary>
    /// For <see cref="AgentMemoryMode.SlidingWindow"/>: how many of the most
    /// recent messages to retain. Ignored by other modes.
    /// </summary>
    public int MaxHistory { get; init; } = 20;

    /// <summary>Optional top-P override. <c>null</c> means use the provider default.</summary>
    public double? TopP { get; init; }

    /// <summary>Optional max output tokens override. <c>null</c> means use the provider default.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>Response format (Default/Text/Json). <c>Default</c> leaves it unset.</summary>
    public AgentResponseFormat ResponseFormat { get; init; } = AgentResponseFormat.Default;

    // —— HarnessAgent provider 开关（仅 HarnessAgent 路径生效；ChatClientAgent 不用这些 provider）。
    //   默认全 false = 全部禁用，保持轻量运行时。按需开启。

    /// <summary>启用 web search provider（默认禁用）。</summary>
    public bool EnableWebSearch { get; init; }

    /// <summary>启用 todo provider（默认禁用）。</summary>
    public bool EnableTodoProvider { get; init; }

    /// <summary>
    /// 要求工具调用前审批（默认禁用）。开启后 Harness 会把该 agent 的所有工具包成
    /// <c>ApprovalRequiredAIFunction</c>，模型调用工具时流里冒出审批请求，UI 弹审批框；
    /// 用户批准后工具执行、拒绝则模型改走他法。复用 MAF <c>ToolApprovalAgent</c> 协作式审批。
    /// </summary>
    public bool RequireToolApproval { get; init; }

    /// <summary>启用 agent mode provider（默认禁用）。</summary>
    public bool EnableAgentModeProvider { get; init; }

    /// <summary>启用 file memory provider（默认禁用）。</summary>
    public bool EnableFileMemory { get; init; }

    /// <summary>启用 OpenTelemetry（默认禁用）。</summary>
    public bool EnableOpenTelemetry { get; init; }

    /// <summary>
    /// 启用长期记忆（默认禁用）。开启后 agent 构建/运行时接入 <c>IMemoryStore</c>：每轮 run 前
    /// 召回该用户的长期记忆注入 system 指令，run 后把本轮 Q&amp;A 沉淀为 <c>TaskHistory</c> 记忆项，
    /// 跨会话保留。区别于 <see cref="Memory"/>（会话内聊天历史窗口）。
    /// </summary>
    public bool EnableLongTermMemory { get; init; }

    /// <summary>
    /// 后台 Agent 白名单：本 agent 可动态分派后台子任务的目标子 agent 名列表。非空即启用
    /// MAF <c>BackgroundAgentsProvider</c>（由宿主经 backgroundAgents 工厂把各名字解析成
    /// <c>AIAgent</c> 构建 provider 注入），让本 agent 在对话里并发委派子任务并收结果。
    /// 仅普通 LLM agent（<c>Engine == Llm</c>），规则/代码规则 agent 不可作子 agent。
    /// </summary>
    public IReadOnlyList<string>? BackgroundAgents { get; init; }
}
