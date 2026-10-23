namespace Aether.AI.Brains.Rule.RulesEngine;

/// <summary>
/// 暴露给 RulesEngine 表达式的只读事实。workflow 里以 <c>brain</c> 为参数名引用，
/// 例如 <c>"Expression": "brain.UserText.StartsWith(\"查\") &amp;&amp; brain.ToolCallCount == 0"</c>。
/// <para>字段刻意保持最小：只暴露大脑决策真正需要的信息。需要更多上下文时，
/// 由宿主在工具侧取，而不是把会话细节塞进事实对象。</para>
/// </summary>
public sealed class RulesEngineFacts
{
    /// <summary>本轮最新 user 消息文本（工具回填消息不算业务输入），无则空串。</summary>
    public string UserText { get; init; } = string.Empty;

    /// <summary>本轮最新工具结果文本；本轮尚无工具结果为 null。</summary>
    public string? LastResult { get; init; }

    /// <summary>本轮已执行的工具调用数（用于把无序规则串成有序步骤）。</summary>
    public int ToolCallCount { get; init; }

    /// <summary>当前会话 id；无会话时为 null。</summary>
    public string? SessionId { get; init; }
}
