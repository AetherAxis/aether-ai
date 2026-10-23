using System.Runtime.CompilerServices;
using Aether.AI.Core.Agents;
using Aether.AI.Harness;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Sessions;
using Aether.AI.Harness.Skills;
using Aether.AI.Harness.Tools;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Tests;

/// <summary>
/// 驱动完整 agent 路径（HarnessRuntime → AgentFactory → HarnessAgent → MAF FICC 循环），
/// 断言关键节点信号（<see cref="RunProgress"/>）确实**由 Harness 发射**（而非聊天 VM）。
/// 这是 RunProgress Step2「信号下沉 Harness」的回归守卫：删掉 HarnessRuntime 里的
/// <c>handlers?.OnProgress</c> 发射点，这些用例即失败。
/// <para>消费者接的是 <see cref="RunProgressFanout"/>（DI 里的默认出口形态），故同时覆盖
/// 「Harness 发射 → fanout 去重 → 消费者收到」的完整链路。</para>
/// </summary>
public class RunProgressSignalTests
{
    private sealed class RecordingSink : IRunProgressSink
    {
        private readonly List<RunProgress> _signals = new();
        public IReadOnlyList<RunProgress> Signals { get { lock (_signals) return _signals.ToList(); } }
        public void Post(RunProgress signal) { lock (_signals) _signals.Add(signal); }
    }

    [Fact]
    public async Task Tool_run_emits_started_finished_and_done()
    {
        var client = new ScriptedChatClient();
        client.Script.Add(MakeCall("call-1"));
        client.Script.Add(MakeText("done"));

        var sink = new RecordingSink();
        var runtime = BuildRuntime(client, new RunProgressFanout().Add(sink));
        var session = runtime.StartSession("echo-agent");

        await runtime.RunStreamingAsync(session.SessionId, "go");

        var kinds = sink.Signals.Select(s => s.Kind).ToList();
        Assert.Contains(RunProgressKind.ToolStarted, kinds);
        Assert.Contains(RunProgressKind.ToolFinished, kinds);
        // 终态由 RunStreamingAsync 包装层发射（而非核心循环）——正常返回 → RunDone。
        Assert.Equal(RunProgressKind.RunDone, kinds[^1]);
        Assert.DoesNotContain(RunProgressKind.RunError, kinds);

        // 工具名要带上（桌宠气泡显示「🔧 Echo」）。
        var started = sink.Signals.First(s => s.Kind == RunProgressKind.ToolStarted);
        Assert.Equal("Echo", started.ToolName);
    }

    [Fact]
    public async Task Reasoning_emits_reasoning_signal()
    {
        var client = new ScriptedChatClient();
        client.Script.Add(new ChatResponseUpdate
        {
            Contents = new AIContent[] { new TextReasoningContent("thinking…") }
        });
        client.Script.Add(MakeText("answer"));

        var sink = new RecordingSink();
        var runtime = BuildRuntime(client, new RunProgressFanout().Add(sink));
        var session = runtime.StartSession("echo-agent");

        await runtime.RunStreamingAsync(session.SessionId, "go");

        Assert.Contains(sink.Signals, s => s.Kind == RunProgressKind.Reasoning);
    }

    [Fact]
    public async Task Error_content_emits_run_error_signal()
    {
        var client = new ScriptedChatClient();
        client.Script.Add(new ChatResponseUpdate
        {
            Contents = new AIContent[] { new ErrorContent("provider hiccup") }
        });

        var sink = new RecordingSink();
        var runtime = BuildRuntime(client, new RunProgressFanout().Add(sink));
        var session = runtime.StartSession("echo-agent");

        await runtime.RunStreamingAsync(session.SessionId, "go");

        var err = sink.Signals.First(s => s.Kind == RunProgressKind.RunError);
        Assert.Equal("provider hiccup", err.Text);
    }

    [Fact]
    public async Task Failing_run_emits_run_error_terminal_instead_of_done()
    {
        // 运行期异常收敛 → 包装层兜底 RunError，消费者不会永远停在「工作」态。
        var client = new ThrowingChatClient();

        var sink = new RecordingSink();
        var runtime = BuildRuntime(client, new RunProgressFanout().Add(sink));
        var session = runtime.StartSession("echo-agent");

        await Assert.ThrowsAnyAsync<Exception>(() => runtime.RunStreamingAsync(session.SessionId, "go"));

        Assert.Contains(sink.Signals, s => s.Kind == RunProgressKind.RunError);
        Assert.DoesNotContain(sink.Signals, s => s.Kind == RunProgressKind.RunDone);
    }

    [Fact]
    public async Task Null_sink_still_runs_normally()
    {
        // 未注册消费者（Automation 老宿主/测试）不得因信号缺席而崩。
        var client = new ScriptedChatClient();
        client.Script.Add(MakeText("hi"));

        var runtime = BuildRuntime(client, sink: null);
        var session = runtime.StartSession("echo-agent");

        var text = await runtime.RunStreamingAsync(session.SessionId, "go");
        Assert.Equal("hi", text);
    }

    // —— helpers（与 ToolExecutionCountTests 同构，此处独立一份避免跨测试类耦合） ——

    private static ChatResponseUpdate MakeCall(string callId) => new()
    {
        Contents = new AIContent[] { new FunctionCallContent(callId, "Echo", new Dictionary<string, object?>()) }
    };

    private static ChatResponseUpdate MakeText(string text) => new()
    {
        Contents = new AIContent[] { new TextContent(text) }
    };

    private static HarnessRuntime BuildRuntime(IChatClient client, IRunProgressSink? sink)
    {
        var registry = new ToolRegistry();
        registry.Register("Echo", "echo tool", "test",
            _ => AIFunctionFactory.Create(() => "ok", new AIFunctionFactoryOptions { Name = "Echo" }),
            ToolApprovalLevel.Safe);
        var factory = new AgentFactory(new ConstChatClientFactory(client), registry, new SkillRegistry());
        var runtime = new HarnessRuntime(factory, new SessionManager(), progressSink: sink);
        runtime.RegisterAgent(new AgentDefinition
        {
            Id = "echo-agent",
            Name = "Agent",
            Instructions = "be brief",
            Tools = new[] { "Echo" },
        });
        return runtime;
    }

    private sealed class ConstChatClientFactory : IChatClientFactory
    {
        private readonly IChatClient _client;
        public ConstChatClientFactory(IChatClient client) => _client = client;
        public IChatClient Create(string? modelId) => _client;
    }

    /// <summary>按脚本逐条返回流式更新的伪 IChatClient；每次 GetStreamingResponseAsync 消费一条（FICC 一次迭代）。</summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        public List<ChatResponseUpdate> Script { get; } = new();
        private int _index;

        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Stream(cancellationToken);

        private async IAsyncEnumerable<ChatResponseUpdate> Stream([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            var i = _index++;
            if (i < Script.Count) yield return Script[i];
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    /// <summary>流一开头就抛的伪 client（模拟 provider 网络错误）。</summary>
    private sealed class ThrowingChatClient : IChatClient
    {
        public void Dispose() { }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("network down");
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("network down");
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }
}
