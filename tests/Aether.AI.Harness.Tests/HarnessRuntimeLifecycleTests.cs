using System.Runtime.CompilerServices;
using Aether.AI.Core.Agents;
using Aether.AI.Core.Messages;
using Aether.AI.Core.Sessions;
using Aether.AI.Harness;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Sessions;
using Aether.AI.Harness.Skills;
using Aether.AI.Harness.Tools;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Tests;

/// <summary>
/// <see cref="HarnessRuntime"/> 的生命周期与运行路径回归守卫。此前该类约 700 行里直接测试只有
/// 2 个注册用例——生命周期方法（<c>Invalidate</c>/<c>RebuildSessionFramework</c>/<c>EvictSession</c>）、
/// 非流式 <c>RunAsync</c>、workflow 重载、模式管理、审批恢复的防御分支全部零覆盖，而它们承载的正是
/// 「编辑 agent 后新定义生效且不丢记忆」等关键不变量。本文件在 HarnessRuntime/AgentFactory 拆分
/// 重构前把这些不变量钉死。
/// <para>与 <see cref="RunProgressSignalTests"/> 的分工：那边管「进度信号由 Harness 发射」，这边管
/// 生命周期/簿记/分支行为。fake client 同构（脚本逐条消费），独立一份避免跨测试类耦合。</para>
/// </summary>
public class HarnessRuntimeLifecycleTests
{
    // —— Invalidate：定义与 agent 缓存的失效 ——

    [Fact]
    public async Task Invalidate_drops_definition_and_cached_agent()
    {
        var (runtime, _, client, spy) = Build();
        client.Script.Add(MakeText("hi"));
        var session = runtime.StartSession("echo-agent");
        await runtime.RunStreamingAsync(session.SessionId, "go"); // 构建并缓存 agent（Create #1）

        runtime.Invalidate("echo-agent");

        Assert.False(runtime.HasAgent("echo-agent"));
        Assert.Throws<InvalidOperationException>(() => runtime.StartSession("echo-agent"));
        // 既有会话的下一轮同样失败（定义已删）——runner 负责在编辑后丢弃会话线程。
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.RunStreamingAsync(session.SessionId, "again"));
        Assert.Single(spy.Models); // 此后无第二次构建
    }

    [Fact]
    public async Task Invalidate_forces_rebuild_with_new_definition()
    {
        var (runtime, _, client, spy) = Build();
        client.Script.Add(MakeText("one"));
        var session = runtime.StartSession("echo-agent");
        await runtime.RunStreamingAsync(session.SessionId, "go");
        Assert.Equal(new[] { "model-a" }, spy.Models);

        // 仅重注册（未 Invalidate）：agent 缓存仍命中旧实例——文档化缓存契约：
        // 改定义必须配 Invalidate，否则新定义（含新 ModelId）不生效。
        runtime.RegisterAgent(NewDefinition("model-b"));
        client.Script.Add(MakeText("two"));
        await runtime.RunStreamingAsync(runtime.StartSession("echo-agent").SessionId, "go2");
        Assert.Single(spy.Models);

        // Invalidate + 重注册 → 下次运行用新定义重建（新 ModelId 到达工厂）。
        runtime.Invalidate("echo-agent");
        runtime.RegisterAgent(NewDefinition("model-b"));
        client.Script.Add(MakeText("three"));
        await runtime.RunStreamingAsync(runtime.StartSession("echo-agent").SessionId, "go3");
        Assert.Equal(new[] { "model-a", "model-b" }, spy.Models);
    }

    // —— RebuildSessionFramework / framework session 缓存 ——

    [Fact]
    public async Task RebuildSessionFramework_keeps_session_but_resets_framework_history()
    {
        var (runtime, _, client, _) = Build();
        client.Script.Add(MakeText("r1"));
        client.Script.Add(MakeText("r2"));
        client.Script.Add(MakeText("r3"));
        var session = runtime.StartSession("echo-agent");

        await runtime.RunStreamingAsync(session.SessionId, "first");
        var firstTurnShape = client.Calls[0].Count; // 首轮 client 看到的消息形状（含框架可能注入的部分）

        // 同一 framework session 复用：第 2 轮首个 client 调用携带第 1 轮历史（多轮记忆）。
        await runtime.RunStreamingAsync(session.SessionId, "second");
        Assert.True(client.Calls[1].Count > firstTurnShape,
            "同一会话第 2 轮应携带第 1 轮历史（framework session 复用）");

        // 编辑 agent 后重建：framework session 丢弃（LLM 历史清零），AetherSession 保留（簿记累计）。
        runtime.RebuildSessionFramework(session.SessionId);
        await runtime.RunStreamingAsync(session.SessionId, "third");
        Assert.Equal(firstTurnShape, client.Calls[2].Count); // 新 framework session = 首轮形状（历史已清）
        Assert.Equal(6, session.Messages.Count); // 3 轮 × (user + assistant)——会话未丢
    }

    // —— EvictSession ——

    [Fact]
    public async Task EvictSession_drops_runtime_state_next_run_throws()
    {
        var (runtime, _, client, _) = Build();
        client.Script.Add(MakeText("hi"));
        var session = runtime.StartSession("echo-agent");
        await runtime.RunStreamingAsync(session.SessionId, "go");

        runtime.EvictSession(session.SessionId);

        // 删除后下一轮立即失败（不静默新建——新建是 runner/宿主的显式职责）。
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.RunStreamingAsync(session.SessionId, "again"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.RunAsync(session.SessionId, "again"));

        // 显式新建会话 → 全新运行（framework session 从零）。
        client.Script.Add(MakeText("fresh"));
        var fresh = runtime.StartSession("echo-agent");
        var text = await runtime.RunStreamingAsync(fresh.SessionId, "hello");
        Assert.Equal("fresh", text);
    }

    // —— RunAsync（非流式路径）——

    [Fact]
    public async Task RunAsync_records_messages_tool_calls_and_idle_state()
    {
        var (runtime, _, client, _) = Build();
        client.Script.Add(MakeCall("call-1"));
        client.Script.Add(MakeText("done"));
        var session = runtime.StartSession("echo-agent");

        var text = await runtime.RunAsync(session.SessionId, "go");

        Assert.Equal("done", text);
        // 非流式路径的工具簿记（ExtractToolCalls 从 AgentResponse 提取）。
        Assert.Equal(new[] { "Echo" }, session.ToolCalls);
        Assert.Equal(2, session.Messages.Count);
        Assert.Equal(MessageRole.User, session.Messages[0].Role);
        Assert.Equal("go", session.Messages[0].Content);
        Assert.Equal(MessageRole.Assistant, session.Messages[1].Role);
        Assert.Equal("done", session.Messages[1].Content);
        Assert.Equal(SessionState.Idle, session.State);
        // FICC 回路：第 2 次 client 调用能看到工具结果回填（"result:ok"，Result 可能 JSON 包装故分别判）。
        Assert.Contains(client.Calls[1], m => m.Contains("result:") && m.Contains("ok"));
    }

    [Fact]
    public async Task RunAsync_faults_session_on_provider_error()
    {
        var (runtime, _, _, _) = Build(client: new ThrowingClient());
        var session = runtime.StartSession("echo-agent");

        // MAF 对 provider 异常的包装形态不作假设（RunProgressSignalTests 同用 ThrowsAnyAsync）。
        await Assert.ThrowsAnyAsync<Exception>(() => runtime.RunAsync(session.SessionId, "go"));

        Assert.Equal(SessionState.Faulted, session.State);
        var err = session.Properties.TryGetValue("lastError", out var e) ? e : null;
        Assert.NotNull(err);
        Assert.Contains("network down", err);
    }

    [Fact]
    public async Task RunStreamingAsync_faults_session_on_provider_error()
    {
        // RunProgressSignalTests 已断言异常收敛发 RunError；这里补会话状态侧的守卫——
        // 状态永远停在 Active 会让后续轮次/UI 误判会话仍在跑（Faulted 分支的注释所述）。
        var (runtime, _, _, _) = Build(client: new ThrowingClient());
        var session = runtime.StartSession("echo-agent");

        await Assert.ThrowsAnyAsync<Exception>(() => runtime.RunStreamingAsync(session.SessionId, "go"));

        Assert.Equal(SessionState.Faulted, session.State);
        Assert.Contains("network down", session.Properties.TryGetValue("lastError", out var e) ? e : "");
    }

    // —— workflow 重载（自带 AIAgent + FrameworkAgentSession，无 AetherSession 簿记）——

    [Fact]
    public async Task Workflow_overload_streams_and_completes_without_session_bookkeeping()
    {
        var sink = new RecordingSink();
        var (runtime, factory, client, _) = Build(sink: sink);
        client.Script.Add(MakeText("hi"));
        var agent = factory.Create(NewDefinition("model-a")); // 任意 AIAgent（workflow 包成的同理）
        var frameworkSession = await agent.CreateSessionAsync();

        var tokens = new List<string>();
        var text = await runtime.RunStreamingAsync(agent, frameworkSession, "go",
            new StreamHandlers { OnToken = (t, _) => { tokens.Add(t); return Task.CompletedTask; } });

        Assert.Equal("hi", text);
        Assert.Equal(new[] { "hi" }, tokens);
        Assert.Equal(RunProgressKind.RunDone, sink.Signals[^1].Kind); // 终态对无簿记路径同样发射
    }

    // —— 模式管理（AgentModeProvider）——

    [Fact]
    public async Task Mode_management_roundtrip_requires_EnableAgentModeProvider()
    {
        // 未启用 provider：Set 返 false、Get 返 null（不抛）。
        var (plain, _, _, _) = Build();
        var plainSession = plain.StartSession("echo-agent");
        Assert.False(await plain.SetModeAsync(plainSession.SessionId, AgentModes.Auto));
        Assert.Null(await plain.GetModeAsync(plainSession.SessionId));

        // 启用 provider：4 档自定义模式（MAF 1.22）逐一 Set → Get 回读；
        // 运行期 desiredMode 亦在 run 开始时生效。
        var (modeful, _, client, _) = Build(enableMode: true);
        var modeSession = modeful.StartSession("echo-agent");
        foreach (var mode in new[] { AgentModes.Manual, AgentModes.AutoEdit, AgentModes.Auto })
        {
            Assert.True(await modeful.SetModeAsync(modeSession.SessionId, mode));
            Assert.Equal(mode, await modeful.GetModeAsync(modeSession.SessionId));
        }

        client.Script.Add(MakeText("ok"));
        await modeful.RunStreamingAsync(modeSession.SessionId, "go", desiredMode: AgentModes.Plan);
        Assert.Equal(AgentModes.Plan, await modeful.GetModeAsync(modeSession.SessionId));
    }

    [Fact]
    public async Task Mode_validation_rejects_unknown_mode_names()
    {
        // MAF 1.22 ValidateMode：不在 4 档模式表内的名字（含旧 "execute"——自定义模式取代默认两态后失效）
        // 抛 ArgumentException——名字漂移快速失败（Core AgentModes 单一来源防散落硬编码的依据）。
        var (modeful, _, _, _) = Build(enableMode: true);
        var session = modeful.StartSession("echo-agent");
        await Assert.ThrowsAsync<ArgumentException>(
            () => modeful.SetModeAsync(session.SessionId, "execute"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => modeful.SetModeAsync(session.SessionId, "get_mode"));
    }

    // —— 审批恢复的防御分支 ——

    [Fact]
    public async Task Approval_response_without_pending_request_concludes_with_RunDone()
    {
        // 审批响应指向不存在的 RequestId（如请求已被 always-approve 规则自动消化）→
        // 本轮无内容、**正常收敛**：返回空串、发 RunDone（而非 RunError——消费者不会停在「工作」态）、
        // 不追加用户消息（审批恢复轮语义）。
        var sink = new RecordingSink();
        var (runtime, _, client, _) = Build(sink: sink);
        client.Script.Add(MakeText("hi"));
        var session = runtime.StartSession("echo-agent");
        await runtime.RunStreamingAsync(session.SessionId, "go");
        Assert.Equal(RunProgressKind.RunDone, sink.Signals[^1].Kind);

        var text = await runtime.RunStreamingAsync(session.SessionId, "ignored",
            approvalResponse: new ToolApprovalDecision("no-such-request", Approved: true, Reason: null, AlwaysApprove: false));

        Assert.Equal(string.Empty, text);
        Assert.Equal(RunProgressKind.RunDone, sink.Signals[^1].Kind);
        Assert.Equal(SessionState.Idle, session.State);
    }

    // —— helpers ——

    private static AgentDefinition NewDefinition(string modelId, bool enableMode = false) => new()
    {
        Id = "echo-agent",
        Name = "Agent",
        Instructions = "be brief",
        ModelId = modelId,
        Tools = new[] { "Echo" },
        Memory = AgentMemoryMode.Full,
        EnableAgentModeProvider = enableMode,
    };

    private static ChatResponseUpdate MakeCall(string callId) => new()
    {
        Contents = new AIContent[] { new FunctionCallContent(callId, "Echo", new Dictionary<string, object?>()) }
    };

    private static ChatResponseUpdate MakeText(string text) => new()
    {
        Contents = new AIContent[] { new TextContent(text) }
    };

    private static (HarnessRuntime Runtime, AgentFactory Factory, ScriptedClient Client, FactorySpy Spy) Build(
        IRunProgressSink? sink = null, bool enableMode = false, IChatClient? client = null)
    {
        var scripted = new ScriptedClient();
        var spy = new FactorySpy(client ?? scripted);
        var registry = new ToolRegistry();
        registry.Register("Echo", "echo tool", "test",
            _ => AIFunctionFactory.Create(() => "ok", new AIFunctionFactoryOptions { Name = "Echo" }),
            ToolApprovalLevel.Safe);
        var factory = new AgentFactory(spy, registry, new SkillRegistry());
        var runtime = new HarnessRuntime(factory, new SessionManager(), progressSink: sink);
        runtime.RegisterAgent(NewDefinition("model-a", enableMode));
        return (runtime, factory, scripted, spy);
    }

    /// <summary>记录每次 Create 的 modelId（观察 agent 缓存重建契约）；client 委托给内部实例。</summary>
    private sealed class FactorySpy : IChatClientFactory
    {
        private readonly IChatClient _inner;
        private readonly List<string?> _models = new();
        public FactorySpy(IChatClient inner) => _inner = inner;
        public IReadOnlyList<string?> Models { get { lock (_models) return _models.ToList(); } }
        public IChatClient Create(string? modelId)
        {
            lock (_models) _models.Add(modelId);
            return _inner;
        }
    }

    /// <summary>双模式脚本伪 client：流式/非流式每次调用各消费一条脚本（FICC 一次迭代）。
    /// 每次调用记录消息快照（role + 文本/调用/结果摘要），供多轮历史与工具回填断言。</summary>
    private sealed class ScriptedClient : IChatClient
    {
        public List<ChatResponseUpdate> Script { get; } = new();
        private int _index;
        private readonly List<List<string>> _calls = new();
        public IReadOnlyList<List<string>> Calls { get { lock (_calls) return _calls.ToList(); } }

        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Record(messages);
            var i = _index++;
            var update = i < Script.Count ? Script[i] : MakeText(string.Empty);
            var msg = new ChatMessage { Role = ChatRole.Assistant };
            foreach (var c in update.Contents) msg.Contents.Add(c);
            return Task.FromResult(new ChatResponse(msg));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Stream(messages, cancellationToken);

        private async IAsyncEnumerable<ChatResponseUpdate> Stream(IEnumerable<ChatMessage> messages, [EnumeratorCancellation] CancellationToken ct)
        {
            Record(messages);
            await Task.Yield();
            var i = _index++;
            if (i < Script.Count) yield return Script[i];
        }

        private void Record(IEnumerable<ChatMessage> messages)
        {
            lock (_calls) _calls.Add(messages.Select(Summarize).ToList());
        }

        private static string Summarize(ChatMessage m)
        {
            var parts = new List<string>();
            foreach (var c in m.Contents)
            {
                if (c is TextContent t && !string.IsNullOrEmpty(t.Text)) parts.Add(t.Text);
                else if (c is FunctionCallContent f) parts.Add($"call:{f.Name}");
                else if (c is FunctionResultContent r) parts.Add($"result:{r.Result}");
            }
            return m.Role + ":" + string.Join("|", parts);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    /// <summary>一调用就抛的伪 client（模拟 provider 网络错误），流式/非流式同抛。</summary>
    private sealed class ThrowingClient : IChatClient
    {
        public void Dispose() { }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("network down");
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("network down");
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    private sealed class RecordingSink : IRunProgressSink
    {
        private readonly List<RunProgress> _signals = new();
        public IReadOnlyList<RunProgress> Signals { get { lock (_signals) return _signals.ToList(); } }
        public void Post(RunProgress signal) { lock (_signals) _signals.Add(signal); }
    }
}
