using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// 兜底代码大脑：代码规则未编译 / 编译失败 / 未注册时，<see cref="AgentFactory.BuildRuleAgent"/>
/// 用它产一句提示终止 agent 循环，避免空 brain 崩溃。对称于 <see cref="RuleProgramBrain"/> 的 NoMatch 兜底。
/// </summary>
internal sealed class ErrorReplyBrain : IRuleBrain
{
    private readonly string _message;

    public ErrorReplyBrain(string? error)
    {
        _message = string.IsNullOrEmpty(error)
            ? "代码规则未就绪：尚未编译，请检查源码或重载。"
            : $"代码规则未就绪：{error}";
    }

    public ValueTask<IReadOnlyList<AIContent>> DecideAsync(RuleContext context, CancellationToken cancellationToken)
        => new(new[] { new TextContent(_message) });
}
