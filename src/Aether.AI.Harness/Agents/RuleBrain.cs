using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// 规则大脑的求值上下文。由 <see cref="RuleChatClient"/> 从对话历史推导（无状态），
/// 供 <see cref="IRuleBrain"/> 决定本步流出什么 <see cref="AIContent"/>。
/// </summary>
public sealed class RuleContext
{
    /// <summary>本轮最新 user 消息（无则为 null）。</summary>
    public ChatMessage? LatestUser { get; init; }

    /// <summary>历史里最新一条工具结果（无则为 null）。</summary>
    public FunctionResultContent? LatestToolResult { get; init; }

    /// <summary>本轮已调工具次数（最后一条 user 消息之后的 FunctionCallContent 计数）。</summary>
    public int ToolCallsThisTurn { get; init; }

    /// <summary>本轮可用的工具（来自 ChatOptions.Tools）。</summary>
    public IReadOnlyList<AIFunction> AvailableTools { get; init; } = Array.Empty<AIFunction>();

    /// <summary>完整对话历史（含 system / 历史 turn / 本轮工具调用与结果）。</summary>
    public IReadOnlyList<ChatMessage> History { get; init; } = Array.Empty<ChatMessage>();
}

/// <summary>
/// 规则大脑：每次 agent 循环被调，返回本步要流出的 <see cref="AIContent"/>（工具调用或终答文本）。
/// 返回空列表表示无输出（终止）。实现须保证收敛（最终返回非工具内容或空），否则 MAF 循环不结束。
/// <para>声明式实现见 <c>RuleProgramBrain</c>；复杂逻辑可另行实现此接口（代码大脑，1.1 注册机制）。</para>
/// </summary>
public interface IRuleBrain
{
    ValueTask<IReadOnlyList<AIContent>> DecideAsync(RuleContext context, CancellationToken cancellationToken);
}
