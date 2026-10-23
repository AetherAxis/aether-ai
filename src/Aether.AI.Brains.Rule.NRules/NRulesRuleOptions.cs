namespace Aether.AI.Brains.Rule.NRules;

/// <summary><c>nRules</c> 规则后端的运行选项。</summary>
public sealed class NRulesRuleOptions
{
    /// <summary>没有任何规则产出动作时的兜底回复。</summary>
    public string NoMatchReply { get; set; } = "无匹配规则";

    /// <summary>单轮最大工具调用数安全闸，防死循环。默认 20，与其他规则后端对齐。</summary>
    public int MaxSteps { get; set; } = 20;
}
