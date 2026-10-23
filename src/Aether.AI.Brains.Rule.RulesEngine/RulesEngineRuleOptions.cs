namespace Aether.AI.Brains.Rule.RulesEngine;

/// <summary>
/// <c>rulesEngine</c> 规则后端的运行选项。对应 Microsoft RulesEngine 的求值语义，
/// 与内置 <c>program</c> 后端（<c>RuleProgram</c>）不是同一套模型。
/// </summary>
public sealed class RulesEngineRuleOptions
{
    /// <summary>
    /// 从 <c>RuleSource</c> 的 workflow 数组中选择一个求值。null/空 = 使用第一个 workflow。
    /// 名称区分大小写，与 RulesEngine 自身一致。
    /// </summary>
    public string? WorkflowName { get; set; }

    /// <summary>没有任何规则产出动作时的兜底回复。</summary>
    public string NoMatchReply { get; set; } = "无匹配规则";

    /// <summary>单轮最大工具调用数安全闸，防死循环。默认 20，与 <c>RuleProgram.MaxSteps</c> 对齐。</summary>
    public int MaxSteps { get; set; } = 20;
}
