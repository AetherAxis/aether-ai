using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// <see cref="IChatClient"/> 的规则大脑实现：代替 LLM 客户端接入 MAF HarnessAgent。
/// 每次 <see cref="GetStreamingResponseAsync"/> 从对话历史推导 <see cref="RuleContext"/>，
/// 交 <see cref="IRuleBrain"/> 决定本步流出的 <see cref="AIContent"/>（工具调用或终答）。
/// <para>工具执行、结果回填、循环终止全由 MAF 框架负责——本类只产 <see cref="AIContent"/>。
/// 对称于 <see cref="ToolFilteringChatClient"/>（同为 <see cref="IChatClient"/> 装饰/替换）。</para>
/// <para>仅支持流式（<see cref="GetResponseAsync"/> 抛 <see cref="NotSupportedException"/>）——
/// agent 运行时走流式路径。</para>
/// </summary>
public sealed class RuleChatClient : IChatClient
{
    private readonly IRuleBrain _brain;
    private readonly ILogger<RuleChatClient>? _logger;

    public RuleChatClient(IRuleBrain brain, ILogger<RuleChatClient>? logger = null)
    {
        _brain = brain ?? throw new ArgumentNullException(nameof(brain));
        _logger = logger;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{nameof(RuleChatClient)} 仅支持流式 (GetStreamingResponseAsync)。");

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var history = messages as IList<ChatMessage> ?? messages.ToList();
        return Stream(history, options, cancellationToken);
    }

    private async IAsyncEnumerable<ChatResponseUpdate> Stream(
        IList<ChatMessage> history,
        ChatOptions? options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var context = BuildContext(history, options);
        var contents = await _brain.DecideAsync(context, ct).ConfigureAwait(false);
        if (contents is not null && contents.Count > 0)
        {
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = contents.ToList(),
            };
        }
        _logger?.LogDebug("RuleBrain decided: {Count} content(s) at turn step {Step}.",
            contents?.Count ?? 0, context.ToolCallsThisTurn);
    }

    /// <summary>从对话历史 + ChatOptions 推导求值上下文（无状态）。</summary>
    private static RuleContext BuildContext(IList<ChatMessage> history, ChatOptions? options)
    {
        // 找最后一条「真正的用户输入」：User 角色 + 不含 FunctionResultContent。
        // 工具结果也是 ChatRole.User（装 FunctionResultContent，无 TextContent）——若取到它，
        // LatestUser.Text 会是空 → 程序级 Capture/{user.N} 替换、toolCallsThisTurn、OnUserRegex 全错。
        // 镜像 SlidingWindowChatReducer「安全 user 起点 = user 角色 + 不含 FunctionResultContent」。
        ChatMessage? latestUser = null;
        var lastUserIndex = -1;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role == ChatRole.User
                && !history[i].Contents.Any(c => c is FunctionResultContent))
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

        // 最新工具结果（整条历史里最后一条 FunctionResultContent）。
        FunctionResultContent? latestResult = null;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            foreach (var c in history[i].Contents)
            {
                if (c is FunctionResultContent r)
                {
                    latestResult = r;
                    break;
                }
            }
            if (latestResult is not null) break;
        }

        var tools = options?.Tools?.OfType<AIFunction>().ToList() ?? new List<AIFunction>();

        return new RuleContext
        {
            LatestUser = latestUser,
            LatestToolResult = latestResult,
            ToolCallsThisTurn = toolCallsThisTurn,
            AvailableTools = tools,
            History = history.ToList(),
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
