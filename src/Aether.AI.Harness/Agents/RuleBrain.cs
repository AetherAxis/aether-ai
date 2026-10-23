using Aether.AI.Brains.Abstractions;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// 规则大脑的求值上下文。由 <see cref="RuleChatClient"/> 从对话历史推导（无状态），
/// 供 <see cref="IRuleBrain"/> 决定本步流出什么 <see cref="AIContent"/>。
/// </summary>
[Obsolete("Use Aether.AI.Brains.Abstractions.BrainContext.")]
public sealed class RuleContext : BrainContext
{
}

/// <summary>
/// 规则大脑：每次 agent 循环被调，返回本步要流出的 <see cref="AIContent"/>（工具调用或终答文本）。
/// 返回空列表表示无输出（终止）。实现须保证收敛（最终返回非工具内容或空），否则 MAF 循环不结束。
/// <para>声明式实现见 <c>RuleProgramBrain</c>；复杂逻辑可另行实现此接口（代码大脑，1.1 注册机制）。</para>
/// </summary>
public interface IRuleBrain : IBrain
{
    ValueTask<IReadOnlyList<AIContent>> DecideAsync(RuleContext context, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<AIContent>> IBrain.DecideAsync(BrainContext context, CancellationToken cancellationToken)
    {
        var compatibilityContext = context as RuleContext ?? new RuleContext
        {
            SessionId = context.SessionId,
            LatestUser = context.LatestUser,
            LatestToolResult = context.LatestToolResult,
            ToolCallsThisTurn = context.ToolCallsThisTurn,
            AvailableTools = context.AvailableTools,
            History = context.History,
        };
        return DecideAsync(compatibilityContext, cancellationToken);
    }
}
