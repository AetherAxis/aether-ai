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
/// 诊断「工具/ask_user 重复执行两遍」的根因。
///
/// 思路：用 ScriptedChatClient（伪 LLM）驱动完整 agent 路径（HarnessRuntime → AgentFactory →
/// HarnessAgent → MAF FICC 循环），精确控制模型每次返回的工具调用数量，断言工具实际执行次数。
///   - 单次调用 → 应只执行一次：证明平台层（host 的 while 循环 / RunStreamingCoreAsync / FICC）
///     不会无端双重执行。若此用例失败（执行 2 次）则说明有平台 bug。
///   - 一次响应里发两个相同调用 → 执行两次：复现「重复两遍」，并证明这是模型输出驱动的
///     （平台忠实执行模型请求的每一次调用），而非平台自发重复。
///   - 跨迭代重复调用 → 执行两次：复现机制 (b)（模型见到结果后下一轮再调一次）。
/// </summary>
public class ToolExecutionCountTests
{
    [Fact]
    public async Task Single_tool_call_executes_exactly_once()
    {
        // 模型：第 1 轮发一次 Echo 调用，第 2 轮给最终文本
        var client = new ScriptedChatClient();
        client.Script.Add(MakeCall("call-1"));
        client.Script.Add(MakeText("done"));

        int calls = 0;
        var runtime = BuildRuntime(client, "Echo", () => { calls++; return "ok"; });
        var session = runtime.StartSession("echo-agent");

        await runtime.RunStreamingAsync(session.SessionId, "go");

        // 关键断言：模型只调一次 → 平台只执行一次。若 =2 则平台有双重执行 bug。
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Duplicate_calls_in_one_response_execute_twice()
    {
        // 机制 (a)：模型在单次响应里发两个相同工具调用（不同 CallId）—— 复现「正好两遍」
        var client = new ScriptedChatClient();
        client.Script.Add(new ChatResponseUpdate
        {
            Contents = new AIContent[]
            {
                new FunctionCallContent("call-1", "Echo", new Dictionary<string, object?>()),
                new FunctionCallContent("call-2", "Echo", new Dictionary<string, object?>()),
            }
        });
        client.Script.Add(MakeText("done"));

        int calls = 0;
        var runtime = BuildRuntime(client, "Echo", () => { calls++; return "ok"; });
        var session = runtime.StartSession("echo-agent");

        await runtime.RunStreamingAsync(session.SessionId, "go");

        // 平台忠实执行模型请求的每一次调用 → 2 次。这把「重复两遍」定位到模型输出侧。
        Assert.Equal(2, calls);
    }

    /// <summary>
    /// 审批路径的执行计数守卫（用户假设：MAF 工具调用状态机可能在「请求审批前」就先执行一次，
    /// 恢复轮再执行一次 → 双重）。用计数器精确钉死：
    ///   - 首轮（无 approval）只应冒出审批请求，工具<b>不应执行</b>（calls==0）。
    ///     若 =1 → 证实「先执行后审批」的状态机行为（双重执行的根因）。
    ///   - 恢复轮（带 approve）执行后，工具总共只执行一次（calls==1）。
    ///     若 =2 → 状态机先执行 + 恢复轮重放 = 双重执行 bug。
    /// 现有 ToolApprovalSpikeTests 只断言 sawResult/sawCall（不计数），无法发现此 bug，故补此用例。
    /// </summary>
    [Fact]
    public async Task Approval_path_executes_tool_exactly_once()
    {
        var client = new ScriptedChatClient();
        client.Script.Add(new ChatResponseUpdate
        {
            Contents = new AIContent[]
            {
                new FunctionCallContent("call-1", "DangerousOp", new Dictionary<string, object?>())
            }
        });
        client.Script.Add(MakeText("done"));

        int calls = 0;
        var runtime = BuildRuntime(client, "DangerousOp", () => { calls++; return "ok"; },
            ToolApprovalLevel.Dangerous, requireToolApproval: true, agentId: "danger-agent");
        var session = runtime.StartSession("danger-agent");

        // 1) 首轮流：应冒出审批请求
        ToolApprovalRequest? approvalReq = null;
        await runtime.RunStreamingAsync(session.SessionId, "go", new StreamHandlers(
            OnApprovalRequest: (req, _) => { approvalReq = req; return Task.CompletedTask; }));

        Assert.NotNull(approvalReq);
        Assert.Equal("DangerousOp", approvalReq!.ToolName);
        // 关键断言①：审批请求发出前，工具不应被执行。
        int callsAfterTurn1 = calls;
        Assert.Equal(0, callsAfterTurn1);

        // 2) 带 approve 决定二次调用恢复
        var decision = new ToolApprovalDecision(approvalReq.RequestId, Approved: true, Reason: null, AlwaysApprove: false);
        await runtime.RunStreamingAsync(session.SessionId, "go", new StreamHandlers(), decision);

        // 关键断言②：恢复后工具总共只执行一次。=2 即 MAF 状态机「先执行后审批 + 恢复重放」双重 bug。
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Repeated_call_across_iterations_executes_twice()
    {
        // 机制 (b)：模型第 1 轮调一次，第 2 轮（已见到结果）再调一次，第 3 轮给文本
        var client = new ScriptedChatClient();
        client.Script.Add(MakeCall("call-1"));
        client.Script.Add(MakeCall("call-2"));
        client.Script.Add(MakeText("done"));

        int calls = 0;
        var runtime = BuildRuntime(client, "Echo", () => { calls++; return "ok"; });
        var session = runtime.StartSession("echo-agent");

        await runtime.RunStreamingAsync(session.SessionId, "go");

        Assert.Equal(2, calls);
    }

    // —— helpers ——

    private static ChatResponseUpdate MakeCall(string callId) => new()
    {
        Contents = new AIContent[]
        {
            new FunctionCallContent(callId, "Echo", new Dictionary<string, object?>())
        }
    };

    private static ChatResponseUpdate MakeText(string text) => new()
    {
        Contents = new AIContent[] { new TextContent(text) }
    };

    private static HarnessRuntime BuildRuntime(IChatClient client, string toolName, Func<string> body,
        ToolApprovalLevel level = ToolApprovalLevel.Safe, bool requireToolApproval = false, string agentId = "echo-agent")
    {
        var registry = new ToolRegistry();
        registry.Register(toolName, "echo tool", "test",
            _ => AIFunctionFactory.Create(body, new AIFunctionFactoryOptions { Name = toolName }), level);
        var factory = new AgentFactory(new ConstChatClientFactory(client), registry, new SkillRegistry());
        var runtime = new HarnessRuntime(factory, new SessionManager());
        runtime.RegisterAgent(new AgentDefinition
        {
            Id = agentId,
            Name = "Agent",
            Instructions = "be brief",
            Tools = new[] { toolName },
            RequireToolApproval = requireToolApproval,
        });
        return runtime;
    }

    private sealed class ConstChatClientFactory : IChatClientFactory
    {
        private readonly IChatClient _client;
        public ConstChatClientFactory(IChatClient client) => _client = client;
        public IChatClient Create(string? modelId) => _client;
    }

    /// <summary>按脚本逐条返回流式更新的伪 IChatClient；每次 GetStreamingResponseAsync 消费一条脚本项（对应 FICC 一次迭代）。</summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        public List<ChatResponseUpdate> Script { get; } = new();
        public List<IList<ChatMessage>> Received { get; } = new();

        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Received.Add(messages.ToList());
            var index = Received.Count - 1;
            return Stream(index, cancellationToken);
        }

        private async IAsyncEnumerable<ChatResponseUpdate> Stream(int index, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            if (index < Script.Count)
                yield return Script[index];
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }
}
