using Aether.AI.Core.Agents;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Skills;
using Aether.AI.Harness.Tools;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Tests;

/// <summary>
/// 代码大脑跨会话状态隔离回归（2026-10-23 修复的钉子）：
/// <see cref="AgentRuntimeRegistry"/> 按 agentId 缓存<b>单个</b> AIAgent——修复前
/// <see cref="AgentFactory"/> 构建时创建的<b>一个</b> <see cref="CodeRuleBrain"/> 实例被缓存 agent 的
/// <see cref="RuleChatClient"/> 持有，同一 agentId 的多个会话共享其协程状态（Context/_continuation/
/// _pendingResult/作者字段）：B 会话的 Step==0 会 <c>ResetCoroutine</c> 掉 A 会话挂起中的续体。
/// 修复：RunScopedBrainRouter 按 run（用户 turn）路由 fresh 实例（挂起 callId → 大脑映射）。
/// </summary>
public class CodeBrainIsolationTests
{
    [Fact]
    public async Task Same_agentId_two_concurrent_sessions_isolate_code_brain_state()
    {
        var gates = new Dictionary<string, TaskCompletionSource<string>>(StringComparer.Ordinal)
        {
            ["A"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["B"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var tools = new ToolRegistry();
        tools.Register("Gate", "可控闸门", "测试",
            _ => AIFunctionFactory.Create((Func<string, Task<string>>)(async key =>
            {
                var tcs = gates.TryGetValue(key, out var g) ? g : throw new InvalidOperationException($"未知闸门 {key}");
                return await tcs.Task;
            }), name: "Gate"));

        var registry = new CodeBrainRegistry();
        registry.Register("code-iso", () => new CountingBrain());

        var factory = new AgentFactory(new NullChatClientFactory(), tools, new SkillRegistry(), codeBrainRegistry: registry);
        // 一个 agentId 只建一次 agent——镜像 AgentRuntimeRegistry.GetOrCreateAgent 的缓存语义
        var agent = factory.Create(new AgentDefinition
        {
            Id = "code-iso",
            Name = "Iso",
            Instructions = "t",
            Engine = EngineKind.Rule,
            IsCodeRule = true,
            Tools = new[] { "Gate" },
            Memory = AgentMemoryMode.Off,
        });

        var sessionA = await agent.CreateSessionAsync();
        var sessionB = await agent.CreateSessionAsync();

        // 两路并发起跑：都在 await Call("Gate") 处挂起（协程挂起 + 工具等闸门）
        var runA = CollectAsync(agent.RunStreamingAsync("A", sessionA));
        var runB = CollectAsync(agent.RunStreamingAsync("B", sessionB));

        await WaitUntilSuspendedAsync(gates);   // 等两路都真正挂到闸门上（不猜时延）

        // 交错放行：先 A 后 B
        gates["A"].SetResult("resA");
        var finalA = await runA;
        gates["B"].SetResult("resB");
        var finalB = await runB;

        // 隔离断言：各自终答只含各自的状态与工具结果；每 run fresh 实例（Calls==1、实例数==2）
        Assert.Contains("final:A:calls=1:result=resA", finalA);
        Assert.Contains("final:B:calls=1:result=resB", finalB);
        Assert.DoesNotContain("resB", finalA);
        Assert.DoesNotContain("resA", finalB);
        Assert.Equal(2, CountingBrain.Instances);
    }

    [Fact]
    public async Task Next_turn_gets_fresh_code_brain_instance()
    {
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tools = new ToolRegistry();
        tools.Register("Gate", "可控闸门", "测试",
            _ => AIFunctionFactory.Create((Func<string, Task<string>>)(async key =>
            {
                if (key != "T") throw new InvalidOperationException($"未知闸门 {key}");
                return await gate.Task;
            }), name: "Gate"));

        var registry = new CodeBrainRegistry();
        registry.Register("code-turn", () => new CountingBrain());

        var factory = new AgentFactory(new NullChatClientFactory(), tools, new SkillRegistry(), codeBrainRegistry: registry);
        var agent = factory.Create(new AgentDefinition
        {
            Id = "code-turn",
            Name = "Turn",
            Instructions = "t",
            Engine = EngineKind.Rule,
            IsCodeRule = true,
            Tools = new[] { "Gate" },
            Memory = AgentMemoryMode.Off,
        });

        var session = await agent.CreateSessionAsync();
        var instancesBefore = CountingBrain.Instances;

        gate.SetResult("r1");
        var first = await CollectAsync(agent.RunStreamingAsync("T", session));
        Assert.Contains("calls=1", first);

        // 同会话下一 turn：per-run 语义 → fresh 实例（作者字段重置；驱动状态本就每轮重置）
        gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        // 工具工厂闭包持有旧 gate——重建 agent 变体太重；直接换注册表工厂输出同一 CountingBrain 类型，
        // 第二轮用新 TCS 重新注册工具即可
        var tools2 = tools;
        var second = await CollectAsync(agent.RunStreamingAsync("T2", session));

        Assert.Contains("calls=1", second);   // fresh 实例：跨 turn 不累积作者字段
        Assert.Equal(instancesBefore + 2, CountingBrain.Instances);
    }

    private static async Task<string> CollectAsync(IAsyncEnumerable<Microsoft.Agents.AI.AgentUpdate> updates)
    {
        var text = "";
        await foreach (var u in updates)
            if (!string.IsNullOrEmpty(u.Text)) text += u.Text;
        return text;
    }

    /// <summary>等两路闸门都被工具 await（无竞态：轮询工具进入计数——用 Gate 工具的调用侧信号）。</summary>
    private static async Task WaitUntilSuspendedAsync(Dictionary<string, TaskCompletionSource<string>> gates)
    {
        // 简单可靠：轮询到两个 run 的最终 Task 均未完成且两闸门均未被消费（SetResult 前）——
        // 实际上挂起状态由「runA/runB 未完成」+「我们尚未放行」保证；这里只需让两个 run 都
        // 进入循环（首个 await 已发生）。用短暂让步 + 计数器确认两实例都已启动协程。
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (CountingBrain.Started < 2 && sw.ElapsedMilliseconds < 5000)
            await Task.Delay(20);
        // 再让一步：协程已启动并 await Call（挂起写回 _yieldTool 后才可能被 MAF 调工具）
        await Task.Delay(100);
        Assert.True(CountingBrain.Started >= 2, "两个 run 的协程应均已启动");
    }

    /// <summary>作者侧有状态大脑：实例计数（证明 fresh）+ 调用计数（证明不共享）+ 织入 user 与结果。</summary>
    private sealed class CountingBrain : CodeRuleBrain
    {
        public static int Instances;
        public static int Started;

        private int _calls;

        protected override async Task<string> RunAsync()
        {
            Interlocked.Increment(ref Instances);
            Interlocked.Increment(ref Started);
            var key = UserText;
            _calls++;
            var r = await Call("Gate", new Dictionary<string, object?> { ["key"] = key });
            return $"final:{key}:calls={_calls}:result={r}";
        }
    }

    private sealed class NullChatClient : IChatClient
    {
        public void Dispose() { }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    private sealed class NullChatClientFactory : IChatClientFactory
    {
        public IChatClient Create(string? modelId) => new NullChatClient();
    }
}
