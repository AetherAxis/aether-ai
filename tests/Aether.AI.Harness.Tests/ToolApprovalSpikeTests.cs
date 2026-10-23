using System.ComponentModel;
using System.Runtime.CompilerServices;
using Aether.AI.Core.Agents;
using Aether.AI.Harness;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Sessions;
using Aether.AI.Harness.Tools;
using Aether.AI.Harness.Skills;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Tests;

/// <summary>
/// Spike：验证 MAF HarnessAgent 的协作式审批流式行为，为聊天页审批弹窗打底。
/// 不依赖 Aether 的 HarnessRuntime 改造，直接驱动 MAF agent：
///   1) 模型（伪 client）吐出对 ApprovalRequiredAIFunction 工具的 FunctionCallContent
///      → 流里应冒出 ToolApprovalRequestContent 且本轮结束（不执行工具）。
///   2) 带 ToolApprovalResponseContent 的 ChatMessage 二次 RunStreamingAsync
///      → 应出现 FunctionResultContent（工具被执行），随后模型给最终文本。
/// 另验证 EnableTodoProvider 时 agent.GetService&lt;TodoProvider&gt;() 可解析、
/// TextReasoningContent 能从 update.Contents 取到。
/// </summary>
public class ToolApprovalSpikeTests
{
    [Fact]
    public async Task Approval_flow_surfaces_request_then_resumes_on_response()
    {
        var client = new ScriptedChatClient();
        // 首次：模型决定调用 GetWeather 工具
        client.Script.Add(new ChatResponseUpdate
        {
            Contents = new AIContent[]
            {
                new FunctionCallContent("call-1", "GetWeather",
                    new Dictionary<string, object?> { ["city"] = "北京" })
            }
        });
        // 批准后 FICC 执行工具、再次询问模型 → 给最终文本
        client.Script.Add(new ChatResponseUpdate
        {
            Contents = new AIContent[] { new TextContent("北京：晴") }
        });

        var tool = AIFunctionFactory.Create((Func<string, string>)GetWeather);
        var approvalTool = new ApprovalRequiredAIFunction(tool);

        var options = new HarnessAgentOptions
        {
            Id = "spike",
            Name = "Spike",
            Description = "spike",
            ChatOptions = new ChatOptions
            {
                Instructions = "be brief",
                Tools = new List<AITool> { approvalTool },
            },
        };

        var agent = client.AsHarnessAgent(options);
        var session = await agent.CreateSessionAsync();

        // 1) 首轮流：应冒出 ToolApprovalRequestContent
        ToolApprovalRequestContent? approvalReq = null;
        await foreach (var update in agent.RunStreamingAsync("查天气", session))
        {
            foreach (var c in update.Contents)
            {
                if (c is ToolApprovalRequestContent req)
                {
                    approvalReq = req;
                }
            }
        }
        Assert.NotNull(approvalReq);
        Assert.Equal("GetWeather", ((FunctionCallContent)approvalReq!.ToolCall).Name);

        // 2) 带 approve 响应二次调用：应执行工具 → FunctionResultContent
        var response = approvalReq.CreateResponse(approved: true, reason: null);
        var responseMsg = new ChatMessage { Role = ChatRole.User };
        responseMsg.Contents.Add((AIContent)response);

        bool sawResult = false;
        string finalText = "";
        await foreach (var update in agent.RunStreamingAsync(new[] { responseMsg }, session))
        {
            foreach (var c in update.Contents)
            {
                if (c is FunctionResultContent) sawResult = true;
            }
            if (!string.IsNullOrEmpty(update.Text)) finalText += update.Text;
        }
        Assert.True(sawResult, "批准后应出现 FunctionResultContent（工具被执行）");
        Assert.Contains("晴", finalText);
    }

    [Fact]
    public async Task TextReasoningContent_is_reachable_in_stream()
    {
        var client = new ScriptedChatClient();
        client.Script.Add(new ChatResponseUpdate
        {
            Contents = new AIContent[]
            {
                new TextReasoningContent("让我想想北京天气"),
                new TextContent("北京：晴"),
            }
        });

        var options = new HarnessAgentOptions
        {
            Id = "spike-r",
            Name = "SpikeR",
            Description = "spike reasoning",
            ChatOptions = new ChatOptions { Instructions = "be brief" },
        };
        var agent = client.AsHarnessAgent(options);
        var session = await agent.CreateSessionAsync();

        bool sawReasoning = false;
        string answerText = "";
        await foreach (var update in agent.RunStreamingAsync("hi", session))
        {
            foreach (var c in update.Contents)
            {
                if (c is TextReasoningContent tr && !string.IsNullOrEmpty(tr.Text)) sawReasoning = true;
            }
            if (!string.IsNullOrEmpty(update.Text)) answerText += update.Text;
        }
        Assert.True(sawReasoning, "TextReasoningContent 应能从 update.Contents 取到");
        Assert.DoesNotContain("让我想想", answerText); // 推理不应混入 update.Text
    }

    [Fact]
    public async Task TodoProvider_is_resolvable_when_enabled()
    {
        var client = new ScriptedChatClient();
        client.Script.Add(new ChatResponseUpdate
        {
            Contents = new AIContent[] { new TextContent("ok") }
        });

        var options = new HarnessAgentOptions
        {
            Id = "spike-t",
            Name = "SpikeT",
            Description = "spike todo",
            DisableTodoProvider = false, // 启用 TodoProvider
            ChatOptions = new ChatOptions { Instructions = "be brief" },
        };
        var agent = client.AsHarnessAgent(options);

        var todo = agent.GetService<TodoProvider>();
        Assert.NotNull(todo);
    }

    [Description("Get the weather for a city.")]
    private static string GetWeather([Description("The city.")] string city) => $"{city}：晴";

    [Description("Place a trading order.")]
    private static string PlaceOrder([Description("The symbol.")] string symbol) => $"已报单 {symbol}";

    /// <summary>
    /// 端到端验证 HarnessRuntime 的审批协作循环：首轮流经 OnApprovalRequest 上抛请求，
    /// 带 ToolApprovalDecision 二次调用恢复 → 工具执行(FunctionResultContent) + 最终文本。
    /// </summary>
    [Fact]
    public async Task HarnessRuntime_approval_loop_surfaces_then_resumes()
    {
        var client = new ScriptedChatClient();
        client.Script.Add(new ChatResponseUpdate
        {
            Contents = new AIContent[]
            {
                new FunctionCallContent("call-1", "PlaceOrder",
                    new Dictionary<string, object?> { ["symbol"] = "IF2406" })
            }
        });
        client.Script.Add(new ChatResponseUpdate
        {
            Contents = new AIContent[] { new TextContent("已报单 IF2406") }
        });

        var registry = new ToolRegistry();
        registry.Register("PlaceOrder", "Place a trading order.", "test",
            _ => AIFunctionFactory.Create((Func<string, string>)PlaceOrder), Aether.AI.Core.Agents.ToolApprovalLevel.Dangerous);
        var factory = new AgentFactory(new ConstChatClientFactory(client), registry, new SkillRegistry());
        var runtime = new HarnessRuntime(factory, new SessionManager());
        runtime.RegisterAgent(new AgentDefinition
        {
            Id = "spike-rt",
            Name = "SpikeRT",
            Instructions = "be brief",
            Tools = new[] { "PlaceOrder" },
            RequireToolApproval = true,
        });

        var session = runtime.StartSession("spike-rt");

        // 1) 首轮流：应冒出审批请求
        ToolApprovalRequest? approvalReq = null;
        await runtime.RunStreamingAsync(session.SessionId, "查天气", new StreamHandlers(
            OnApprovalRequest: (req, _) => { approvalReq = req; return Task.CompletedTask; }));

        Assert.NotNull(approvalReq);
        Assert.Equal("PlaceOrder", approvalReq!.ToolName);

        // 2) 带 approve 决定二次调用：应执行工具并给最终文本
        var decision = new ToolApprovalDecision(approvalReq.RequestId, Approved: true, Reason: null, AlwaysApprove: false);
        bool sawResult = false;
        bool sawCall = false;
        string finalText = "";
        await runtime.RunStreamingAsync(session.SessionId, "查天气", new StreamHandlers(
            OnToken: (t, _) => { finalText += t; return Task.CompletedTask; },
            OnToolEvent: (ev, _) =>
            {
                if (ev.Kind == ToolStreamEventKind.Result) sawResult = true;
                else if (ev.Kind == ToolStreamEventKind.Call) sawCall = true;
                return Task.CompletedTask;
            }),
            decision);

        Assert.True(sawResult, "批准后应出现工具结果事件");
        // 恢复轮 FICC 重新发出已批准工具的 Call（UI 据此建工具卡）+ Result（回填）。
        Assert.True(sawCall, "恢复轮应重新发出工具 Call 事件（UI 才能建工具步卡片）");
        Assert.Contains("报单", finalText);
    }

    /// <summary>
    /// 审批恢复轮在 SlidingWindow + 小窗口下仍能正常工作：待审批的 tool_use 是历史里最新的
    /// assistant 消息，reducer 不会截断它；恢复轮发出的 ToolApprovalResponseContent 能正确绑定。
    /// 回归守卫：SlidingWindowChatReducer 的配对感知截断不应破坏审批协作循环。
    /// </summary>
    [Fact]
    public async Task HarnessRuntime_approval_loop_works_under_sliding_window()
    {
        var client = new ScriptedChatClient();
        // 先两轮纯文本把历史堆过 MaxHistory，迫使恢复轮走 reducer 截断路径
        client.Script.Add(new ChatResponseUpdate { Contents = new AIContent[] { new TextContent("ok1") } });
        client.Script.Add(new ChatResponseUpdate { Contents = new AIContent[] { new TextContent("ok2") } });
        // 第三轮：调用需审批的 PlaceOrder
        client.Script.Add(new ChatResponseUpdate
        {
            Contents = new AIContent[]
            {
                new FunctionCallContent("call-1", "PlaceOrder",
                    new Dictionary<string, object?> { ["symbol"] = "IF2406" })
            }
        });
        // 恢复轮：最终文本
        client.Script.Add(new ChatResponseUpdate { Contents = new AIContent[] { new TextContent("已报单 IF2406") } });

        var registry = new ToolRegistry();
        registry.Register("PlaceOrder", "Place a trading order.", "test",
            _ => AIFunctionFactory.Create((Func<string, string>)PlaceOrder), ToolApprovalLevel.Dangerous);
        var factory = new AgentFactory(new ConstChatClientFactory(client), registry, new SkillRegistry());
        var runtime = new HarnessRuntime(factory, new SessionManager());
        runtime.RegisterAgent(new AgentDefinition
        {
            Id = "spike-sw",
            Name = "SpikeSW",
            Instructions = "be brief",
            Tools = new[] { "PlaceOrder" },
            RequireToolApproval = true,
            Memory = AgentMemoryMode.SlidingWindow,
            MaxHistory = 4, // 小窗口：恢复轮历史经 reducer 处理
        });

        var session = runtime.StartSession("spike-sw");

        // 轮 1、2：纯文本堆积历史
        await runtime.RunStreamingAsync(session.SessionId, "t1");
        await runtime.RunStreamingAsync(session.SessionId, "t2");

        // 轮 3：应冒出审批请求（tool_use 是历史最新消息，reducer 不会截断）
        ToolApprovalRequest? approvalReq = null;
        await runtime.RunStreamingAsync(session.SessionId, "t3", new StreamHandlers(
            OnApprovalRequest: (req, _) => { approvalReq = req; return Task.CompletedTask; }));
        Assert.NotNull(approvalReq);

        // 恢复轮：批准 → 应执行工具并给最终文本
        var decision = new ToolApprovalDecision(approvalReq!.RequestId, Approved: true, Reason: null, AlwaysApprove: false);
        bool sawResult = false;
        string finalText = "";
        await runtime.RunStreamingAsync(session.SessionId, "t3", new StreamHandlers(
            OnToken: (t, _) => { finalText += t; return Task.CompletedTask; },
            OnToolEvent: (ev, _) => { if (ev.Kind == ToolStreamEventKind.Result) sawResult = true; return Task.CompletedTask; }),
            decision);

        Assert.True(sawResult, "SlidingWindow 下批准后应出现工具结果事件");
        Assert.Contains("报单", finalText);
    }

    private sealed class ConstChatClientFactory : IChatClientFactory
    {
        private readonly IChatClient _client;
        public ConstChatClientFactory(IChatClient client) => _client = client;
        public IChatClient Create(string? modelId) => _client;
    }

    /// <summary>按脚本逐条返回流式更新的伪 IChatClient；每次 GetStreamingResponseAsync 消费一条脚本项。</summary>
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
