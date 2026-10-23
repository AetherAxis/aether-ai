using System.Collections.Concurrent;
using Aether.AI.Brains.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Aether.AI.Brains.Code;

/// <summary>Keeps one stateful code-brain instance per logical user turn.</summary>
public sealed class RunScopedBrainRouter : IBrain, ISessionStateOwner
{
    private readonly Func<IBrain> _factory;
    private readonly ILogger<RunScopedBrainRouter>? _logger;
    private readonly ConcurrentDictionary<PendingKey, PendingRun> _pendingByCallId = new();

    public RunScopedBrainRouter(Func<IBrain> factory, ILogger<RunScopedBrainRouter>? logger = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger;
    }

    public async ValueTask<IReadOnlyList<AIContent>> DecideAsync(
        BrainContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        PendingRun pending;
        if (context.ToolCallsThisTurn == 0)
        {
            pending = new PendingRun(_factory());
        }
        else
        {
            var callId = context.LatestToolResult?.CallId;
            var key = PendingKey.Create(context.SessionId, callId);
            if (key is null || !_pendingByCallId.TryRemove(key, out pending!))
            {
                return Error("代码大脑运行状态已失效，请重新发起本轮操作。");
            }
            pending.CancellationRegistration.Dispose();
        }

        IReadOnlyList<AIContent> contents;
        try
        {
            contents = await pending.Brain.DecideAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Code brain decision failed.");
            return Error($"代码大脑执行失败：{ex.Message}");
        }

        var calls = contents.OfType<FunctionCallContent>().ToArray();
        if (calls.Length == 0) return contents;
        if (calls.Length != 1 || string.IsNullOrWhiteSpace(calls[0].CallId))
            return Error("代码大脑每个步骤必须产生一个带 CallId 的工具调用。");

        var nextCallId = calls[0].CallId!;
        var nextKey = new PendingKey(context.SessionId, nextCallId);
        if (!_pendingByCallId.TryAdd(nextKey, pending))
            return Error($"代码大脑工具调用标识冲突：{nextCallId}");

        pending.CancellationRegistration = cancellationToken.Register(
            static state =>
            {
                var cleanup = (CancellationCleanup)state!;
                cleanup.Owner._pendingByCallId.TryRemove(cleanup.Key, out _);
            },
            new CancellationCleanup(this, nextKey));
        return contents;
    }

    public void EvictSession(string sessionId)
    {
        foreach (var entry in _pendingByCallId)
        {
            if (!string.Equals(entry.Key.SessionId, sessionId, StringComparison.Ordinal)) continue;
            if (_pendingByCallId.TryRemove(entry.Key, out var pending))
                pending.CancellationRegistration.Dispose();
        }
    }

    private static IReadOnlyList<AIContent> Error(string message)
        => new AIContent[] { new TextContent(message) };

    private sealed class PendingRun(IBrain brain)
    {
        public IBrain Brain { get; } = brain;
        public CancellationTokenRegistration CancellationRegistration { get; set; }
    }

    private sealed record PendingKey(string? SessionId, string CallId)
    {
        public static PendingKey? Create(string? sessionId, string? callId)
            => string.IsNullOrWhiteSpace(callId) ? null : new PendingKey(sessionId, callId);
    }

    private sealed record CancellationCleanup(RunScopedBrainRouter Owner, PendingKey Key);
}
