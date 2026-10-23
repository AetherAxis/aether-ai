using Aether.AI.Core.Agents;
using Microsoft.Extensions.AI;

namespace Aether.AI.Brains.Abstractions;

/// <summary>
/// 从 MAF 对话历史推导 <see cref="BrainContext"/> 的唯一实现。所有非 LLM 大脑适配器
/// （<see cref="BrainChatClient"/>、路由客户端）都必须经此构造上下文——两处各自实现时
/// 「哪条消息算本轮用户输入」的判定会漂移（审批响应、工具结果也带 User 角色）。
/// </summary>
public static class BrainContextBuilder
{
    /// <summary>
    /// 是否是承载业务输入的 user 消息：User 角色且含非空文本。审批响应（
    /// <c>ToolApprovalResponseContent</c>）与工具结果不含文本，故不被误判为本轮输入。
    /// </summary>
    public static bool IsBusinessUserMessage(ChatMessage message)
        => message.Role == ChatRole.User
            && message.Contents.OfType<TextContent>().Any(content => !string.IsNullOrWhiteSpace(content.Text));

    /// <summary>按历史 + 本轮 ChatOptions 构造上下文（无状态、不产生副作用）。</summary>
    public static BrainContext Build(IList<ChatMessage> history, ChatOptions? options)
    {
        ArgumentNullException.ThrowIfNull(history);

        // 审批响应和工具结果也可能使用 User 角色；只有携带文本的 User 消息才是业务输入。
        ChatMessage? latestUser = null;
        var lastUserIndex = -1;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (IsBusinessUserMessage(history[i]))
            {
                latestUser = history[i];
                lastUserIndex = i;
                break;
            }
        }

        // 本轮已调工具次数 = 最后一条真正用户输入之后的 FunctionCallContent 计数。
        var toolCallsThisTurn = 0;
        if (lastUserIndex >= 0)
        {
            for (var i = lastUserIndex + 1; i < history.Count; i++)
            {
                toolCallsThisTurn += history[i].Contents.Count(c => c is FunctionCallContent);
            }
        }

        // 最新工具结果必须属于当前 turn。扫描整个历史会让下一轮误读上一轮结果。
        FunctionResultContent? latestResult = null;
        for (var i = history.Count - 1; i > lastUserIndex; i--)
        {
            for (var contentIndex = history[i].Contents.Count - 1; contentIndex >= 0; contentIndex--)
            {
                if (history[i].Contents[contentIndex] is FunctionResultContent result)
                {
                    latestResult = result;
                    break;
                }
            }
            if (latestResult is not null) break;
        }

        return new BrainContext
        {
            SessionId = AgentExecutionContext.CurrentConversationId.Value,
            LatestUser = latestUser,
            LatestToolResult = latestResult,
            ToolCallsThisTurn = toolCallsThisTurn,
            AvailableTools = options?.Tools?.OfType<AIFunction>().ToList() ?? new List<AIFunction>(),
            History = history.ToList(),
        };
    }
}