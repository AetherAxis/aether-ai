using System.Globalization;
using Aether.AI.Brains.Abstractions;
using Microsoft.Extensions.AI;

namespace Aether.AI.Brains.Rule.NRules;

/// <summary>
/// 插入 NRules 会话的事实对象：规则以它为匹配目标，并通过 <see cref="Reply"/> /
/// <see cref="CallTool"/> 声明本步动作。
/// <para>NRules 的 <c>Then().Do(...)</c> 接收表达式树，因此发射接口刻意做成「一次调用即可完成」：
/// <c>d.CallTool("Echo", "text", d.UserText)</c>、<c>d.Reply($"结果：{d.LastResult}")</c>。
/// 每步只接受第一个动作；后续动作会被忽略并计数，由适配器记 warning。</para>
/// </summary>
public sealed class NRulesDecisionContext
{
    private readonly Dictionary<string, object?> _arguments = new(StringComparer.Ordinal);
    private string? _tool;
    private string? _reply;
    private int _emissions;

    internal NRulesDecisionContext(BrainContext context)
    {
        UserText = context.LatestUser?.Text ?? string.Empty;
        LastResult = context.LatestToolResult?.Result?.ToString();
        ToolCallCount = context.ToolCallsThisTurn;
        SessionId = context.SessionId;
    }

    /// <summary>本轮最新 user 消息文本（工具回填消息不算业务输入），无则空串。</summary>
    public string UserText { get; }

    /// <summary>本轮最新工具结果文本；本轮尚无工具结果为 null。</summary>
    public string? LastResult { get; }

    /// <summary>本轮已执行的工具调用数（用于把规则串成有序步骤）。</summary>
    public int ToolCallCount { get; }

    /// <summary>当前会话 id；无会话时为 null。</summary>
    public string? SessionId { get; }

    /// <summary>便捷判断：本轮是否已有工具结果。</summary>
    public bool HasToolResult => LastResult is not null;

    /// <summary>声明终答（终止本 agent 的循环）。本步已有动作时忽略。</summary>
    public void Reply(string text)
    {
        if (Record())
        {
            _reply = text;
        }
    }

    /// <summary>
    /// 声明工具调用。<paramref name="nameValuePairs"/> 为「参数名, 值」成对展开；参数值可为 null。
    /// 例：<c>d.CallTool("Echo", "text", d.UserText)</c>。本步已有动作时忽略。
    /// </summary>
    public void CallTool(string tool, params object?[] nameValuePairs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        ArgumentNullException.ThrowIfNull(nameValuePairs);

        if (nameValuePairs.Length % 2 != 0)
        {
            throw new ArgumentException(
                $"CallTool('{tool}') 需要成对的「参数名, 值」，实际收到 {nameValuePairs.Length} 项。",
                nameof(nameValuePairs));
        }

        if (!Record())
        {
            return;
        }

        _tool = tool;
        for (var i = 0; i < nameValuePairs.Length; i += 2)
        {
            var name = nameValuePairs[i] as string
                ?? throw new ArgumentException($"CallTool('{tool}') 第 {i / 2 + 1} 个参数名不是字符串。", nameof(nameValuePairs));
            _arguments[name] = nameValuePairs[i + 1];
        }
    }

    /// <summary>本步声明的动作数（含被忽略的后续动作）。</summary>
    internal int Emissions => _emissions;

    internal IReadOnlyList<AIContent> ToContents(string noMatchReply)
    {
        if (!string.IsNullOrEmpty(_reply))
        {
            return new[] { new TextContent(_reply) };
        }

        if (!string.IsNullOrEmpty(_tool))
        {
            var callId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            return new[] { new FunctionCallContent(callId, _tool, new Dictionary<string, object?>(_arguments, StringComparer.Ordinal)) };
        }

        return new[] { new TextContent(noMatchReply) };
    }

    private bool Record() => _emissions++ == 0;
}
