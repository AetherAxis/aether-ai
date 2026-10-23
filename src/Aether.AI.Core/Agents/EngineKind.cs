namespace Aether.AI.Core.Agents;

/// <summary>
/// Agent 大脑类型。<see cref="Llm"/>（默认）经 <c>IChatClientFactory</c> 解析模型客户端；
/// <see cref="Rule"/> 用确定性规则引擎代替 LLM（见 <c>RuleChatClient</c>），复用同一套 agent 生态。
/// </summary>
public enum EngineKind
{
    /// <summary>LLM 大脑（默认，既有行为）。</summary>
    Llm,

    /// <summary>规则大脑：无 LLM，由 <c>RuleProgram</c> 驱动工具调用序列。</summary>
    Rule,
}
