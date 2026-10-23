using Aether.AI.Brains.Abstractions;
using Aether.AI.Brains.Code;
using Aether.AI.Brains.Code.Compilation.CSharp;
using Aether.AI.Harness.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Tests;

/// <summary>
/// 代码规则大脑（<see cref="CodeRuleBrain"/>）测试：验证「C# 源码 → Roslyn 编译 → 代码大脑」管线，
/// 及代码大脑经 <see cref="BrainChatClient"/> + <c>AsHarnessAgent</c> 驱动 MAF agent 循环（工具执行 →
/// 结果回填 → 终答终止）。镜像 <see cref="RuleAgentTests"/> 的端到端刺探，区别在大脑是 C# 代码而非 YAML 规则程序。
/// </summary>
public class CodeRuleBrainTests
{
    // —— Roslyn 编译 ——

    [Fact]
    public async Task RuleScriptCompiler_trycompile_success()
    {
        var source = @"
using System.Threading.Tasks;
using Aether.AI.Brains.Code;

public class CompiledEcho : CodeRuleBrain
{
    protected override Task<string> RunAsync()
        => Task.FromResult(""hi "" + UserText);
}";
        var ok = RuleScriptCompiler.TryCompile(source, out var brain, out var error);

        Assert.True(ok, error ?? "(no error)");
        Assert.NotNull(brain);
        Assert.Null(error);

        // 编译产物是 CodeRuleBrain 子类——喂上下文验证它确实按源码逻辑回终答。
        var ctx = new BrainContext { LatestUser = new ChatMessage(ChatRole.User, "world") };
        var output = await brain!.DecideAsync(ctx, default);
        Assert.Equal("hi world", Assert.Single(output.OfType<TextContent>()).Text);
    }

    [Fact]
    public void RuleScriptCompiler_trycompile_error()
    {
        // 缺分号 + 未闭合大括号 → 编译诊断
        var source = @"
using System.Threading.Tasks;
using Aether.AI.Brains.Code;

public class Broken : CodeRuleBrain
{
    protected override Task<string> RunAsync()
        return ""x""
}";
        var ok = RuleScriptCompiler.TryCompile(source, out var brain, out var error);

        Assert.False(ok);
        Assert.Null(brain);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void RuleScriptCompiler_trycompile_no_brain_subtype()
    {
        // 合法 C# 但无 CodeRuleBrain 子类 → 错误
        var source = "public class Plain { }";
        var ok = RuleScriptCompiler.TryCompile(source, out var brain, out var error);

        Assert.False(ok);
        Assert.Null(brain);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void RuleScriptCompiler_ctor_throw_returns_error_not_exception()
    {
        // 回归守卫：TryCompile 契约「失败 out error，不抛异常」。修复前 brain 类 ctor 抛异常会从
        // Activator.CreateInstance 直接逃逸——一条坏笔记中断整轮 LoadAsync 循环（后续规则不注册、
        // RulesLoaded 不触发、异常经 ContinueWith 未观察）。
        var source = @"
using System;
using System.Threading.Tasks;
using Aether.AI.Brains.Code;

public class ThrowingCtorBrain : CodeRuleBrain
{
    public ThrowingCtorBrain() => throw new InvalidOperationException(""init boom"");
    protected override Task<string> RunAsync() => Task.FromResult(""never"");
}";
        var ok = RuleScriptCompiler.TryCompile(source, out var brain, out var error);

        Assert.False(ok);
        Assert.Null(brain);
        Assert.NotNull(error);
        Assert.Contains("ThrowingCtorBrain", error!);
        Assert.Contains("init boom", error!);
    }

    // —— 代码大脑驱动 MAF agent 循环 ——

    [Fact]
    public async Task CodeRuleBrain_drives_agent_loop()
    {
        // 用户消息原样调 Echo → await 拿回结果 → 终答。验证协程 Call 挂起/恢复闭环。
        var echo = AIFunctionFactory.Create((Func<string, string>)Echo);
        var client = new BrainChatClient(new ToolThenReplyBrain());
        var options = new HarnessAgentOptions
        {
            Id = "code-rule-spike",
            Name = "CodeRuleSpike",
            Description = "code rule agent spike",
            // 规则大脑不消费注入式 NL 上下文——关 TodoProvider，否则它追加 todo user 消息污染 LatestUser。
            DisableTodoProvider = true,
            ChatOptions = new ChatOptions { Instructions = "code", Tools = new List<AITool> { echo } },
        };
        var agent = client.AsHarnessAgent(options);
        var session = await agent.CreateSessionAsync();

        bool sawResult = false;
        var finalText = "";
        await foreach (var update in agent.RunStreamingAsync("600000", session))
        {
            foreach (var c in update.Contents)
            {
                if (c is FunctionResultContent) sawResult = true;
            }
            if (!string.IsNullOrEmpty(update.Text)) finalText += update.Text;
        }

        Assert.True(sawResult, "MAF 应执行 Echo 并回填 FunctionResultContent");
        Assert.Equal("结果：echo:600000", finalText);
    }

    [Fact]
    public async Task CodeRuleBrain_is_stateful_across_steps()
    {
        // 调 Echo 前 _captured 存「UserText + "_step0"」（字段跨 await 挂起/恢复存活）；
        // await 拿回结果后用 _captured（字段，非可从 LatestUser 推导）+ 工具结果终答。
        // 终答必含 "_step0"——只有字段持久化才可能；并含 "echo:abc"——证明 Call 结果正确回喂。
        var echo = AIFunctionFactory.Create((Func<string, string>)Echo);
        var client = new BrainChatClient(new StatefulBrain());
        var options = new HarnessAgentOptions
        {
            Id = "code-rule-stateful",
            Name = "CodeRuleStateful",
            Description = "stateful code rule",
            DisableTodoProvider = true,
            ChatOptions = new ChatOptions { Instructions = "code", Tools = new List<AITool> { echo } },
        };
        var agent = client.AsHarnessAgent(options);
        var session = await agent.CreateSessionAsync();

        var finalText = "";
        await foreach (var update in agent.RunStreamingAsync("abc", session))
        {
            if (!string.IsNullOrEmpty(update.Text)) finalText += update.Text;
        }

        // 字段持久化：含 "_step0"；Call 结果回喂：含 "echo:abc"。
        Assert.Contains("abc_step0", finalText);
        Assert.Contains("echo:abc", finalText);
    }

    [Fact]
    public async Task CodeRuleBrain_chains_multiple_calls_in_order()
    {
        // 协程式最自然的写法：两次 await Call 顺序执行，第二次用第一次的结果做参数。
        // 状态机式要靠 Step 计数分支；协程式就是线性 await。终答 = 串联两次 echo。
        var echo = AIFunctionFactory.Create((Func<string, string>)Echo);
        var client = new BrainChatClient(new ChainedBrain());
        var options = new HarnessAgentOptions
        {
            Id = "code-rule-chain",
            Name = "CodeRuleChain",
            Description = "chained code rule",
            DisableTodoProvider = true,
            ChatOptions = new ChatOptions { Instructions = "code", Tools = new List<AITool> { echo } },
        };
        var agent = client.AsHarnessAgent(options);
        var session = await agent.CreateSessionAsync();

        var finalText = "";
        int resultCount = 0;
        await foreach (var update in agent.RunStreamingAsync("hi", session))
        {
            foreach (var c in update.Contents)
                if (c is FunctionResultContent) resultCount++;
            if (!string.IsNullOrEmpty(update.Text)) finalText += update.Text;
        }

        // 两次工具调用都执行回填；终答是第二次的结果（echo:echo:hi）。
        Assert.Equal(2, resultCount);
        Assert.Equal("echo:echo:hi", finalText);
    }

    [Fact]
    public async Task Ordinary_await_then_return_waits_for_completion()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = new AwaitThenReturnBrain(gate.Task).DecideAsync(new BrainContext(), default).AsTask();

        Assert.False(decision.IsCompleted);
        gate.SetResult();

        var contents = await decision;
        Assert.Equal("ok", Assert.Single(contents.OfType<TextContent>()).Text);
    }

    [Fact]
    public async Task Ordinary_await_then_call_yields_after_completion()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var brain = new AwaitThenCallBrain(gate.Task);
        var decision = brain.DecideAsync(new BrainContext(), default).AsTask();

        Assert.False(decision.IsCompleted);
        gate.SetResult();

        var call = Assert.Single((await decision).OfType<FunctionCallContent>());
        var final = await brain.DecideAsync(new BrainContext
        {
            ToolCallsThisTurn = 1,
            LatestToolResult = new FunctionResultContent(call.CallId, "echoed"),
        }, default);
        Assert.Equal("echoed", Assert.Single(final.OfType<TextContent>()).Text);
    }

    [Fact]
    public async Task Call_then_ordinary_await_then_call_preserves_coroutine()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var brain = new CallAwaitCallBrain(gate.Task);
        var first = Assert.Single((await brain.DecideAsync(new BrainContext(), default)).OfType<FunctionCallContent>());
        var secondDecision = brain.DecideAsync(new BrainContext
        {
            ToolCallsThisTurn = 1,
            LatestToolResult = new FunctionResultContent(first.CallId, "one"),
        }, default).AsTask();

        Assert.False(secondDecision.IsCompleted);
        gate.SetResult();

        var second = Assert.Single((await secondDecision).OfType<FunctionCallContent>());
        var final = await brain.DecideAsync(new BrainContext
        {
            ToolCallsThisTurn = 2,
            LatestToolResult = new FunctionResultContent(second.CallId, "two"),
        }, default);
        Assert.Equal("one|two", Assert.Single(final.OfType<TextContent>()).Text);
    }

    [Fact]
    public async Task Cancellation_during_ordinary_await_runs_finally()
    {
        using var cancellation = new CancellationTokenSource();
        var brain = new CancelableBrain();
        var decision = brain.DecideAsync(new BrainContext(), cancellation.Token).AsTask();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decision);
        Assert.True(brain.CleanedUp);
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_call_runs_finally()
    {
        using var cancellation = new CancellationTokenSource();
        var brain = new CallCleanupBrain();
        Assert.Single((await brain.DecideAsync(new BrainContext(), cancellation.Token)).OfType<FunctionCallContent>());

        cancellation.Cancel();

        await brain.Cleanup.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(brain.CleanedUp);
    }

    private static string Echo(string text) => $"echo:{text}";

    private sealed class AwaitThenReturnBrain(Task gate) : CodeRuleBrain
    {
        protected override async Task<string> RunAsync()
        {
            await gate;
            return "ok";
        }
    }

    private sealed class AwaitThenCallBrain(Task gate) : CodeRuleBrain
    {
        protected override async Task<string> RunAsync()
        {
            await gate;
            return await Call("Echo");
        }
    }

    private sealed class CallAwaitCallBrain(Task gate) : CodeRuleBrain
    {
        protected override async Task<string> RunAsync()
        {
            var first = await Call("Echo");
            await gate;
            var second = await Call("Echo");
            return $"{first}|{second}";
        }
    }

    private sealed class CancelableBrain : CodeRuleBrain
    {
        public bool CleanedUp { get; private set; }

        protected override async Task<string> RunAsync()
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, RunCancellationToken);
                return "unreachable";
            }
            finally
            {
                CleanedUp = true;
            }
        }
    }

    private sealed class CallCleanupBrain : CodeRuleBrain
    {
        private readonly TaskCompletionSource _cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Cleanup => _cleanup.Task;
        public bool CleanedUp { get; private set; }

        protected override async Task<string> RunAsync()
        {
            try
            {
                return await Call("Echo");
            }
            finally
            {
                CleanedUp = true;
                _cleanup.TrySetResult();
            }
        }
    }

    /// <summary>await Call(Echo) → 用 await 返回值终答。证明协程挂起/恢复闭环。</summary>
    private sealed class ToolThenReplyBrain : CodeRuleBrain
    {
        protected override async Task<string> RunAsync()
        {
            var result = await Call("Echo", new { text = UserText });
            return "结果：" + result;
        }
    }

    /// <summary>Call 前存字段；await 后用字段 + 结果终答（证明字段跨 await 持久化）。</summary>
    private sealed class StatefulBrain : CodeRuleBrain
    {
        private string? _captured;
        protected override async Task<string> RunAsync()
        {
            _captured = UserText + "_step0"; // await 前写入；读不到则证明字段未跨挂起存活
            var result = await Call("Echo", new { text = UserText });
            return (_captured ?? "?") + "|" + result;
        }
    }

    /// <summary>两次 await Call 顺序执行，第二次用第一次结果做参数（协程式线性流程）。</summary>
    private sealed class ChainedBrain : CodeRuleBrain
    {
        protected override async Task<string> RunAsync()
        {
            var first = await Call("Echo", new { text = UserText });
            var second = await Call("Echo", new { text = first });
            return second;
        }
    }
}
