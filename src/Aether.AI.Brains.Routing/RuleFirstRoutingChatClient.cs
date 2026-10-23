using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Aether.AI.Brains.Abstractions;
using Aether.AI.Core.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Aether.AI.Brains.Routing;

/// <summary>Explicit rule-first routing: match without side effects, then pin the selected handler for the turn.</summary>
public sealed class RuleFirstRoutingChatClient : IChatClient, ISessionStateOwner
{
    private readonly AgentDefinition _definition;
    private readonly IReadOnlyList<IBrainMatcher> _matchers;
    private readonly IBrain _ruleBrain;
    private readonly Lazy<IChatClient?> _llm;
    private readonly ILogger<RuleFirstRoutingChatClient>? _logger;
    private readonly ConcurrentDictionary<HandlerKey, HandlerSelection> _handlersByCallId = new();

    public RuleFirstRoutingChatClient(
        AgentDefinition definition,
        IEnumerable<IBrainMatcher> matchers,
        IBrain ruleBrain,
        Func<IChatClient?> llmFactory,
        ILogger<RuleFirstRoutingChatClient>? logger = null)
    {
        _definition = definition;
        _matchers = matchers.OrderByDescending(m => m.Priority).ToArray();
        _ruleBrain = ruleBrain;
        _llm = new Lazy<IChatClient?>(llmFactory, LazyThreadSafetyMode.ExecutionAndPublication);
        _logger = logger;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Routing client supports streaming only.");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var history = messages as IList<ChatMessage> ?? messages.ToList();
        var context = BuildContext(history, options);
        var handler = ResolveHandler(context);

        if (handler == HandlerKind.Rule)
        {
            var contents = await _ruleBrain.DecideAsync(context, cancellationToken).ConfigureAwait(false);
            TrackCalls(contents, HandlerKind.Rule, context.SessionId, cancellationToken);
            if (contents.Count > 0)
                yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = contents.ToList() };
            yield break;
        }

        if (handler == HandlerKind.StateLost)
        {
            yield return Text("路由运行状态已失效，请重新发起本轮操作。");
            yield break;
        }

        var llm = _llm.Value;
        if (llm is null)
        {
            yield return Text("没有匹配的规则，且未配置 LLM 大脑。");
            yield break;
        }

        await foreach (var update in llm.GetStreamingResponseAsync(history, options, cancellationToken)
            .ConfigureAwait(false))
        {
            TrackCalls(update.Contents, HandlerKind.Llm, context.SessionId, cancellationToken);
            yield return update;
        }
    }

    private HandlerKind ResolveHandler(BrainContext context)
    {
        var resultIds = CurrentResultCallIds(context);
        if (resultIds.Count > 0)
        {
            HandlerKind? selected = null;
            foreach (var callId in resultIds)
            {
                if (!_handlersByCallId.TryRemove(new HandlerKey(context.SessionId, callId), out var mapped))
                    return HandlerKind.StateLost;
                mapped.CancellationRegistration.Dispose();
                if (selected is null) selected = mapped.Handler;
                else if (selected != mapped.Handler) return HandlerKind.StateLost;
            }
            return selected ?? HandlerKind.StateLost;
        }

        var input = context.LatestUser;
        if (input is null) return HandlerKind.StateLost;
        foreach (var matcher in _matchers)
        {
            var result = matcher.Match(_definition, input);
            _logger?.LogDebug("Brain route matcher {Matcher}: {Result} ({Reason})", matcher.Id, result.Kind, result.Reason);
            if (result.Kind is BrainMatchKind.Matched or BrainMatchKind.NeedsInput) return HandlerKind.Rule;
        }
        return HandlerKind.Llm;
    }

    private void TrackCalls(
        IEnumerable<AIContent> contents,
        HandlerKind handler,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        foreach (var call in contents.OfType<FunctionCallContent>())
        {
            if (string.IsNullOrWhiteSpace(call.CallId)) continue;
            var callId = call.CallId;
            var key = new HandlerKey(sessionId, callId);
            var selection = new HandlerSelection(handler);
            _handlersByCallId[key] = selection;
            selection.CancellationRegistration = cancellationToken.Register(
                static state =>
                {
                    var cleanup = (CancellationCleanup)state!;
                    cleanup.Owner._handlersByCallId.TryRemove(cleanup.Key, out _);
                },
                new CancellationCleanup(this, key));
            if (!_handlersByCallId.TryGetValue(key, out var current)
                || !ReferenceEquals(current, selection))
            {
                selection.CancellationRegistration.Dispose();
            }
        }
    }

    public void EvictSession(string sessionId)
    {
        foreach (var entry in _handlersByCallId)
        {
            if (!string.Equals(entry.Key.SessionId, sessionId, StringComparison.Ordinal)) continue;
            if (_handlersByCallId.TryRemove(entry.Key, out var removed))
                removed.CancellationRegistration.Dispose();
        }

        if (_ruleBrain is ISessionStateOwner ruleState) ruleState.EvictSession(sessionId);
    }

    private static IReadOnlyList<string> CurrentResultCallIds(BrainContext context)
    {
        var start = context.LatestUser is null ? 0 : IndexOfReference(context.History, context.LatestUser) + 1;
        return context.History
            .Skip(start)
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .Select(result => result.CallId)
            .Where(callId => !string.IsNullOrWhiteSpace(callId))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static int IndexOfReference(IReadOnlyList<ChatMessage> history, ChatMessage target)
    {
        for (var i = history.Count - 1; i >= 0; i--)
            if (ReferenceEquals(history[i], target)) return i;
        return -1;
    }

    private static ChatResponseUpdate Text(string value) => new()
    {
        Role = ChatRole.Assistant,
        Contents = new List<AIContent> { new TextContent(value) },
    };

    private static BrainContext BuildContext(IList<ChatMessage> history, ChatOptions? options)
    {
        ChatMessage? latestUser = null;
        var lastUserIndex = -1;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role == ChatRole.User && !history[i].Contents.Any(c => c is FunctionResultContent))
            {
                latestUser = history[i];
                lastUserIndex = i;
                break;
            }
        }

        var toolCalls = 0;
        FunctionResultContent? latestResult = null;
        for (var i = lastUserIndex + 1; i < history.Count; i++)
        {
            toolCalls += history[i].Contents.Count(c => c is FunctionCallContent);
            latestResult = history[i].Contents.OfType<FunctionResultContent>().LastOrDefault() ?? latestResult;
        }

        return new BrainContext
        {
            SessionId = AgentExecutionContext.CurrentConversationId.Value,
            LatestUser = latestUser,
            LatestToolResult = latestResult,
            ToolCallsThisTurn = toolCalls,
            AvailableTools = options?.Tools?.OfType<AIFunction>().ToArray() ?? Array.Empty<AIFunction>(),
            History = history.ToArray(),
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }

    private sealed class HandlerSelection(HandlerKind handler)
    {
        public HandlerKind Handler { get; } = handler;
        public CancellationTokenRegistration CancellationRegistration { get; set; }
    }
    private sealed record HandlerKey(string? SessionId, string CallId);
    private sealed record CancellationCleanup(RuleFirstRoutingChatClient Owner, HandlerKey Key);
    private enum HandlerKind { Rule, Llm, StateLost }
}
