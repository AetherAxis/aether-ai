using Aether.AI.Brains.Abstractions;
using Aether.AI.Brains.Llm;
using Aether.AI.Brains.Routing;
using Aether.AI.Brains.Rule;
using Aether.AI.Core.Agents;
using Aether.AI.Harness;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Sessions;
using Aether.AI.Harness.Skills;
using Aether.AI.Harness.Tools;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Tests;

/// <summary>
/// 大脑契约收敛后的钉子：
/// ① LLM 与 Rule/Code 走同一 <see cref="IBrainFactory"/> 接缝（不再按 EngineKind 分支）；
/// ② 能力集（<see cref="BrainCapabilities"/>）而非引擎枚举决定 Harness 接线；
/// ③ 非流式 <c>RunAsync</c> 对确定性大脑可用；
/// ④ 路由的多步工具链不再因历史里的旧结果误判 StateLost；
/// ⑤ 定义与大脑不一致时显式报错/告警，而非静默忽略。
/// </summary>
public class BrainContractTests
{
    // —— ① 统一接缝 ——

    [Fact]
    public void Llm_is_a_registered_brain_with_full_capabilities()
    {
        var factory = new LlmBrainFactory(new StubChatClientFactory());
        var definition = new AgentDefinition { Id = "llm", Engine = EngineKind.Llm };

        Assert.True(factory.CanCreate(definition));
        Assert.Equal(BrainCapabilitiesDefaults.Llm, factory.Capabilities);
        Assert.True(factory.Capabilities.HasFlag(BrainCapabilities.NaturalLanguageContext));
        Assert.True(factory.Capabilities.HasFlag(BrainCapabilities.Sampling));
        // LLM 的传输就是模型客户端本身，不经 IBrain 适配器。
        Assert.IsType<StubClient>(factory.CreateChatClient(definition));
    }

    [Fact]
    public void Rule_transport_wraps_the_brain_and_forwards_session_eviction()
    {
        var factory = new RuleBrainFactory();
        var definition = new AgentDefinition
        {
            Id = "rule",
            Engine = EngineKind.Rule,
            Rules = new RuleProgram
            {
                Steps = new() { new RuleStep { Action = new() { ReplyText = "ok" } } },
            },
        };

        var client = factory.CreateChatClient(definition);

        Assert.IsType<BrainChatClient>(client);
        Assert.IsAssignableFrom<ISessionStateOwner>(client);
        Assert.False(factory.Capabilities.HasFlag(BrainCapabilities.NaturalLanguageContext));
    }

    [Fact]
    public void Default_content_factory_transport_wraps_the_brain_without_extra_code()
    {
        // 第三方新增确定性大脑时只需实现 Create；传输由 IContentBrainFactory 的默认实现提供。
        IContentBrainFactory factory = new MinimalContentBrainFactory();

        var client = factory.CreateChatClient(new AgentDefinition { Id = "minimal", Engine = EngineKind.Code });

        Assert.IsType<BrainChatClient>(client);
    }

    [Fact]
    public void Unregistered_engine_falls_back_to_engine_kind_capabilities()
    {
        IBrainFactoryRegistry registry = new BrainFactoryRegistry();

        Assert.Equal(BrainCapabilitiesDefaults.Llm,
            registry.CapabilitiesFor(new AgentDefinition { Id = "llm", Engine = EngineKind.Llm }));
        Assert.False(registry.CapabilitiesFor(new AgentDefinition { Id = "rule", Engine = EngineKind.Rule })
            .HasFlag(BrainCapabilities.NaturalLanguageContext));
    }

    // —— ③ 非流式路径 ——

    [Fact]
    public async Task BrainChatClient_supports_non_streaming_responses()
    {
        var client = new BrainChatClient(new FixedBrain("non-stream"));

        var response = await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") });

        Assert.Equal("non-stream", response.Text);
    }

    [Fact]
    public async Task HarnessRuntime_RunAsync_drives_a_rule_agent_with_tools()
    {
        var tools = new ToolRegistry();
        tools.Register("Echo", "回显", "测试",
            _ => AIFunctionFactory.Create((Func<string, string>)(text => $"echo:{text}"), "Echo"));
        var brains = new BrainFactoryRegistry();
        brains.Register(new RuleBrainFactory());
        var factory = new AgentFactory(new NullChatClientFactory(), tools, new SkillRegistry(), brainFactories: brains);
        var runtime = new HarnessRuntime(factory, new SessionManager());

        runtime.RegisterAgent(new AgentDefinition
        {
            Id = "rule-async",
            Name = "RuleAsync",
            Engine = EngineKind.Rule,
            Tools = new[] { "Echo" },
            Rules = new RuleProgram
            {
                Steps = new()
                {
                    new RuleStep
                    {
                        Match = new() { OnUserRegex = "^(.+)$", MaxToolCalls = 0 },
                        Action = new() { Tool = "Echo", Args = new() { ["text"] = "{user.1}" } },
                    },
                    new RuleStep
                    {
                        Match = new() { MinToolCalls = 1 },
                        Action = new() { ReplyText = "结果：{result}" },
                    },
                },
            },
        });

        var session = runtime.StartSession("rule-async");
        var text = await runtime.RunAsync(session.SessionId, "hello");

        Assert.Equal("结果：echo:hello", text);
        Assert.Equal(new[] { "Echo" }, session.ToolCalls);
    }

    [Fact]
    public async Task Routing_supports_non_streaming_for_rule_and_llm()
    {
        var ruleClient = new RuleFirstRoutingChatClient(
            RouteDefinition(), new IBrainMatcher[] { new RuleProgramMatcher("route") },
            new FixedBrain("rule"), () => null);
        Assert.Equal("rule", (await ruleClient.GetResponseAsync(User("查600000"))).Text);

        var llm = new StubClient("llm");
        var routed = new RuleFirstRoutingChatClient(
            RouteDefinition(), new IBrainMatcher[] { new RuleProgramMatcher("route") },
            new FixedBrain("rule"), () => llm);
        Assert.Equal("llm", (await routed.GetResponseAsync(User("普通问题"))).Text);
    }

    // —— ④ 多步工具链 ——

    [Fact]
    public async Task Routing_consumes_only_pending_call_results_across_multiple_tool_steps()
    {
        // 每一步产出一个新 CallId；历史里会同时存在更早步骤已消费的结果。
        // 修复前第二个工具步骤会因重复扫描旧结果而误判 StateLost。
        var brain = new ChainedToolBrain("call-1", "call-2", "done");
        var client = new RuleFirstRoutingChatClient(
            RouteDefinition(), new IBrainMatcher[] { new RuleProgramMatcher("route") }, brain, () => null);
        var history = new List<ChatMessage> { new(ChatRole.User, "查600000") };

        Assert.Equal("call-1", await FirstCallId(client, history));
        history.Add(AssistantCall("call-1"));
        history.Add(ToolResult("call-1", "ok-1"));

        Assert.Equal("call-2", await FirstCallId(client, history));
        history.Add(AssistantCall("call-2"));
        history.Add(ToolResult("call-2", "ok-2"));

        Assert.Equal("done", await Collect(client, history));
    }

    // —— ⑤ 定义校验 ——

    [Fact]
    public void Routing_on_non_llm_engine_is_an_error()
    {
        var issues = AgentDefinitionValidation.Check(new AgentDefinition
        {
            Id = "bad",
            Engine = EngineKind.Rule,
            EnableBrainRouting = true,
        });

        Assert.Contains(issues, issue => issue.Code == "RoutingRequiresLlm" && issue.IsError);
        Assert.Throws<InvalidOperationException>(() => AgentDefinitionValidation.ThrowIfInvalid(
            new AgentDefinition { Id = "bad", Engine = EngineKind.Rule, EnableBrainRouting = true }));
    }

    [Fact]
    public void Llm_only_settings_on_a_deterministic_brain_are_reported()
    {
        var issues = AgentDefinitionValidation.Check(
            new AgentDefinition
            {
                Id = "rule",
                Engine = EngineKind.Rule,
                ModelId = "gpt",
                Temperature = 0.7,
                EnableLongTermMemory = true,
                Rules = new RuleProgram { Steps = new() { new RuleStep { Action = new() { ReplyText = "ok" } } } },
            },
            BrainCapabilitiesDefaults.Deterministic);

        Assert.Equal(3, issues.Count(issue => issue.Code == "SettingIgnoredByBrain"));
        Assert.DoesNotContain(issues, issue => issue.IsError);
    }

    [Fact]
    public void Rules_without_routing_on_an_llm_agent_are_reported()
    {
        var issues = AgentDefinitionValidation.Check(new AgentDefinition
        {
            Id = "llm",
            Engine = EngineKind.Llm,
            Rules = new RuleProgram { Steps = new() { new RuleStep { Action = new() { ReplyText = "x" } } } },
        }, BrainCapabilitiesDefaults.Llm);

        Assert.Contains(issues, issue => issue.Code == "RulesIgnoredWithoutRouting" && !issue.IsError);
    }

    // —— 共享上下文构造 ——

    [Fact]
    public void Protocol_user_messages_are_not_treated_as_business_input()
    {
        var business = new ChatMessage(ChatRole.User, "hi");
        var protocol = new ChatMessage(ChatRole.User, "");
        protocol.Contents.Add(new FunctionResultContent("call-1", "result"));
        var callOnly = new ChatMessage(ChatRole.User, "");
        callOnly.Contents.Add(new FunctionCallContent("call-2", "Echo", new Dictionary<string, object?>()));

        var context = BrainContextBuilder.Build(new List<ChatMessage> { business, protocol, callOnly }, null);

        Assert.Same(business, context.LatestUser);
        Assert.Equal(1, context.ToolCallsThisTurn);
        Assert.Equal("call-1", context.LatestToolResult?.CallId);
    }

    // —— 辅助 ——

    private static AgentDefinition RouteDefinition() => new()
    {
        Id = "route",
        Engine = EngineKind.Llm,
        EnableBrainRouting = true,
        Rules = new RuleProgram
        {
            Steps = new()
            {
                new RuleStep { Match = new() { OnUserRegex = @"^查\d+$", MaxToolCalls = 0 }, Action = new() { ReplyText = "rule" } },
            },
        },
    };

    private static ChatMessage[] User(string text) => new[] { new ChatMessage(ChatRole.User, text) };

    private static async Task<string> Collect(IChatClient client, IEnumerable<ChatMessage> history)
    {
        var output = "";
        await foreach (var update in client.GetStreamingResponseAsync(history)) output += update.Text;
        return output;
    }

    private static async Task<string?> FirstCallId(IChatClient client, IEnumerable<ChatMessage> history)
    {
        await foreach (var update in client.GetStreamingResponseAsync(history))
        {
            foreach (var call in update.Contents.OfType<FunctionCallContent>()) return call.CallId;
        }
        return null;
    }

    private static ChatMessage AssistantCall(string callId)
    {
        var message = new ChatMessage(ChatRole.Assistant, "");
        message.Contents.Add(new FunctionCallContent(callId, "Echo", new Dictionary<string, object?>()));
        return message;
    }

    private static ChatMessage ToolResult(string callId, string result)
    {
        var message = new ChatMessage(ChatRole.Tool, "");
        message.Contents.Add(new FunctionResultContent(callId, result));
        return message;
    }

    private sealed class FixedBrain(string text) : IBrain
    {
        public ValueTask<IReadOnlyList<AIContent>> DecideAsync(BrainContext context, CancellationToken cancellationToken)
            => new(new AIContent[] { new TextContent(text) });
    }

    private sealed class ChainedToolBrain(string firstCallId, string secondCallId, string finalText) : IBrain
    {
        public ValueTask<IReadOnlyList<AIContent>> DecideAsync(BrainContext context, CancellationToken cancellationToken)
        {
            var callId = context.ToolCallsThisTurn switch
            {
                0 => firstCallId,
                1 => secondCallId,
                _ => null,
            };
            IReadOnlyList<AIContent> contents = callId is null
                ? new AIContent[] { new TextContent(finalText) }
                : new AIContent[] { new FunctionCallContent(callId, "Echo", new Dictionary<string, object?>()) };
            return new(contents);
        }
    }

    private sealed class MinimalContentBrainFactory : IContentBrainFactory
    {
        public EngineKind Kind => EngineKind.Code;
        public BrainCapabilities Capabilities => BrainCapabilities.Tools;
        public bool CanCreate(AgentDefinition definition) => false;
        public IBrain Create(AgentDefinition definition) => new FixedBrain("minimal");
    }

    private sealed class StubChatClientFactory : IChatClientFactory
    {
        public IChatClient Create(string? modelId) => new StubClient("stub");
    }

    private sealed class StubClient(string text) : IChatClient
    {
        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = new List<AIContent> { new TextContent(text) },
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    private sealed class NullChatClientFactory : IChatClientFactory
    {
        public IChatClient Create(string? modelId) => new NullClient();
    }

    private sealed class NullClient : IChatClient
    {
        public void Dispose() { }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Null client");
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Null client");
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }
}