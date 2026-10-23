using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Aether.AI.Brains.Abstractions;

/// <summary>
/// 把 <see cref="IBrain"/> 适配成 MAF <see cref="IChatClient"/>：每次调用从对话历史推导
/// <see cref="BrainContext"/>，由大脑决定本步产出的 <see cref="AIContent"/>（工具调用或终答）。
/// <para>工具执行、结果回填、循环终止全由 MAF 框架负责——本类只产 <see cref="AIContent"/>。
/// 流式与非流式（<see cref="GetResponseAsync"/>）都支持，因此 <c>HarnessRuntime.RunAsync</c>
/// 对规则/代码大脑同样可用。</para>
/// <para>会话淘汰时把调用转发给实现了 <see cref="ISessionStateOwner"/> 的内层大脑
/// （如代码大脑的 <c>RunScopedBrainRouter</c>），Harness 只持有适配器即可完成清理。</para>
/// </summary>
public class BrainChatClient : IChatClient, ISessionStateOwner
{
    private readonly IBrain _brain;
    private readonly ILogger? _logger;

    public BrainChatClient(IBrain brain, ILogger<BrainChatClient>? logger = null)
    {
        _brain = brain ?? throw new ArgumentNullException(nameof(brain));
        _logger = logger;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var history = messages as IList<ChatMessage> ?? messages.ToList();
        var context = BrainContextBuilder.Build(history, options);
        var contents = await _brain.DecideAsync(context, cancellationToken).ConfigureAwait(false);
        _logger?.LogDebug("Brain decided: {Count} content(s) at turn step {Step}.",
            contents?.Count ?? 0, context.ToolCallsThisTurn);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents?.ToList() ?? new List<AIContent>()));
    }

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
        var context = BrainContextBuilder.Build(history, options);
        var contents = await _brain.DecideAsync(context, ct).ConfigureAwait(false);
        if (contents is not null && contents.Count > 0)
        {
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = contents.ToList(),
            };
        }
        _logger?.LogDebug("Brain decided: {Count} content(s) at turn step {Step}.",
            contents?.Count ?? 0, context.ToolCallsThisTurn);
    }

    /// <inheritdoc />
    public void EvictSession(string sessionId)
    {
        if (_brain is ISessionStateOwner owner) owner.EvictSession(sessionId);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}