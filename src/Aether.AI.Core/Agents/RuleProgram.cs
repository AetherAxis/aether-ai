namespace Aether.AI.Core.Agents;

/// <summary>
/// 声明式规则程序：有序步骤列表，每步 <see cref="RuleStep"/>（match → action）。由
/// <c>RuleProgramBrain</c> 求值，无 LLM。作为 <see cref="AgentDefinition.Rules"/> 对象全程传递，
/// 不再经 JSON 中转（YAML → <c>RuleProgramYaml</c> 规整成对象 → 直接喂大脑）。
/// <para>求值无状态：每轮从对话历史推导上下文（最新 user 消息 / 最新工具结果 / 本轮已调工具次数），
/// 自上而下取首个命中的步骤执行，保证收敛（<see cref="MaxSteps"/> + <see cref="NoMatchReply"/>）。</para>
/// </summary>
public sealed class RuleProgram
{
    /// <summary>程序名（展示/日志用）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>有序步骤。自上而下首匹配执行。</summary>
    public List<RuleStep> Steps { get; set; } = new();

    /// <summary>无步骤命中时的兜底回复（流出后终止循环）。null = 默认「无匹配规则」。</summary>
    public string? NoMatchReply { get; set; }

    /// <summary>单轮最大工具调用数安全闸，防死循环。默认 20。</summary>
    public int MaxSteps { get; set; } = 20;

    /// <summary>
    /// 程序级捕获正则：对每轮最新 user 消息预匹配一次，其捕获组供所有步骤的
    /// <see cref="RuleAction.Args"/>/<see cref="RuleAction.ReplyText"/> 模板 <c>{user.N}</c> 引用。
    /// 与各步 <see cref="RuleMatch.OnUserRegex"/> 互补：步骤有自己的 OnUserRegex 时用步骤捕获组，
    /// 否则回退到此处。null = 无程序级捕获。典型用法：固定从 user 消息提取参数（如文件路径），多步复用免重复。
    /// </summary>
    public string? Capture { get; set; }
}

/// <summary>一条规则步骤：匹配条件 + 动作。</summary>
public sealed class RuleStep
{
    /// <summary>步骤名（展示/日志用，可选）。</summary>
    public string? Name { get; set; }

    public RuleMatch Match { get; set; } = new();

    public RuleAction Action { get; set; } = new();
}

/// <summary>
/// 匹配条件。所有非 null 字段须同时满足才命中（全 null = always）。
/// </summary>
public sealed class RuleMatch
{
    /// <summary>
    /// 正则匹配本轮最新 user 消息文本。其捕获组供 <see cref="RuleAction.Args"/>/
    /// <see cref="RuleAction.ReplyText"/> 模板 <c>{user.N}</c> 引用。null = 不限 user 消息
    /// （步骤无自己的 OnUserRegex 时，可由 <see cref="RuleProgram.Capture"/> 程序级捕获兜底）。
    /// </summary>
    public string? OnUserRegex { get; set; }

    /// <summary>正则匹配最新工具结果文本。null = 不限工具结果。</summary>
    public string? OnResultRegex { get; set; }

    /// <summary>本轮已调工具次数 ≥ 此值才命中（用于固定流程步进）。null = 不限下界。</summary>
    public int? MinToolCalls { get; set; }

    /// <summary>本轮已调工具次数 ≤ 此值才命中。null = 不限上界。</summary>
    public int? MaxToolCalls { get; set; }
}

/// <summary>
/// 命中后的动作。<see cref="Tool"/> 与 <see cref="ReplyText"/> 互斥：前者继续循环，后者终止。
/// 两者皆空则穿透到下一条步骤。
/// </summary>
public sealed class RuleAction
{
    /// <summary>工具名（非空 → 流出 FunctionCallContent，循环继续）。须在 agent 的工具池里。</summary>
    public string? Tool { get; set; }

    /// <summary>
    /// 结构化工具参数（键→值）。值为字符串时支持模板替换：<c>{user.N}</c>（OnUserRegex/Capture 第 N 捕获组）、
    /// <c>{user}</c>、<c>{result}</c>（上一工具结果原串）；非字符串值原样透传。仅 <see cref="Tool"/> 非空时使用。
    /// </summary>
    public Dictionary<string, object?>? Args { get; set; }

    /// <summary>终答文本（非空 → 流出 TextContent，终止循环）。支持同上模板。与 <see cref="Tool"/> 互斥。</summary>
    public string? ReplyText { get; set; }
}
