using Aether.AI.Brains.Abstractions;
using Aether.AI.Brains.Code;
using Microsoft.Extensions.AI;

namespace Aether.AI.Brains.Code.Tests;

public class RunScopedBrainRouterTests
{
    [Fact]
    public async Task Tool_result_resumes_same_brain_instance()
    {
        var instances = 0;
        var router = new RunScopedBrainRouter(() => new TwoStepBrain(Interlocked.Increment(ref instances)));

        var first = await router.DecideAsync(Context("session-1"), default);
        var call = Assert.Single(first.OfType<FunctionCallContent>());
        var second = await router.DecideAsync(Context("session-1", call.CallId, "ok", 1), default);

        Assert.Equal("instance=1;result=ok", Assert.Single(second.OfType<TextContent>()).Text);
        Assert.Equal(1, instances);
    }

    [Fact]
    public async Task Same_call_id_is_isolated_by_session()
    {
        var instances = 0;
        var router = new RunScopedBrainRouter(() => new TwoStepBrain(Interlocked.Increment(ref instances)));
        await router.DecideAsync(Context("session-1"), default);
        await router.DecideAsync(Context("session-2"), default);

        var first = await router.DecideAsync(Context("session-1", "call-1", "one", 1), default);
        var second = await router.DecideAsync(Context("session-2", "call-1", "two", 1), default);

        Assert.Equal("instance=1;result=one", Assert.Single(first.OfType<TextContent>()).Text);
        Assert.Equal("instance=2;result=two", Assert.Single(second.OfType<TextContent>()).Text);
    }

    [Fact]
    public async Task Evict_session_discards_pending_continuation()
    {
        var router = new RunScopedBrainRouter(() => new TwoStepBrain(1));
        var first = await router.DecideAsync(Context("session-1"), default);
        var call = Assert.Single(first.OfType<FunctionCallContent>());

        router.EvictSession("session-1");
        var resumed = await router.DecideAsync(Context("session-1", call.CallId, "ok", 1), default);

        Assert.Contains("状态已失效", Assert.Single(resumed.OfType<TextContent>()).Text);
    }

    [Fact]
    public async Task Cancellation_discards_pending_continuation()
    {
        using var cancellation = new CancellationTokenSource();
        var router = new RunScopedBrainRouter(() => new TwoStepBrain(1));
        var first = await router.DecideAsync(Context("session-1"), cancellation.Token);
        var call = Assert.Single(first.OfType<FunctionCallContent>());

        cancellation.Cancel();
        var resumed = await router.DecideAsync(Context("session-1", call.CallId, "ok", 1), default);

        Assert.Contains("状态已失效", Assert.Single(resumed.OfType<TextContent>()).Text);
    }

    [Fact]
    public async Task Evict_session_cancels_pending_code_brain_and_runs_finally()
    {
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var router = new RunScopedBrainRouter(() => new CleanupBrain(cleanup));
        Assert.Single((await router.DecideAsync(Context("session-cleanup"), default)).OfType<FunctionCallContent>());

        router.EvictSession("session-cleanup");

        await cleanup.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Cancellation_cancels_pending_code_brain_and_runs_finally()
    {
        using var cancellation = new CancellationTokenSource();
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var router = new RunScopedBrainRouter(() => new CleanupBrain(cleanup));
        Assert.Single((await router.DecideAsync(Context("session-cancel"), cancellation.Token)).OfType<FunctionCallContent>());

        cancellation.Cancel();

        await cleanup.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Repeated_call_id_is_rejected_after_atomic_resume()
    {
        var router = new RunScopedBrainRouter(() => new TwoStepBrain(1));
        var first = await router.DecideAsync(Context("session-1"), default);
        var call = Assert.Single(first.OfType<FunctionCallContent>());

        await router.DecideAsync(Context("session-1", call.CallId, "ok", 1), default);
        var repeated = await router.DecideAsync(Context("session-1", call.CallId, "again", 1), default);

        Assert.Contains("状态已失效", Assert.Single(repeated.OfType<TextContent>()).Text);
    }

    private static BrainContext Context(
        string sessionId,
        string? callId = null,
        string? result = null,
        int calls = 0) => new()
    {
        SessionId = sessionId,
        LatestUser = new ChatMessage(ChatRole.User, "run"),
        LatestToolResult = callId is null ? null : new FunctionResultContent(callId, result),
        ToolCallsThisTurn = calls,
    };

    private sealed class CleanupBrain(TaskCompletionSource cleanup) : CodeRuleBrain
    {
        protected override async Task<string> RunAsync()
        {
            try
            {
                return await Call("Echo");
            }
            finally
            {
                cleanup.TrySetResult();
            }
        }
    }

    private sealed class TwoStepBrain(int instance) : IBrain
    {
        public ValueTask<IReadOnlyList<AIContent>> DecideAsync(
            BrainContext context, CancellationToken cancellationToken)
        {
            IReadOnlyList<AIContent> contents = context.LatestToolResult is null
                ? new AIContent[]
                {
                    new FunctionCallContent("call-1", "Echo", new Dictionary<string, object?>()),
                }
                : new AIContent[]
                {
                    new TextContent($"instance={instance};result={context.LatestToolResult.Result}"),
                };
            return new(contents);
        }
    }
}
