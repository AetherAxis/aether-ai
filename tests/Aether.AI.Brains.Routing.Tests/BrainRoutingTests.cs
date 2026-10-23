using Aether.AI.Brains.Abstractions;
using Aether.AI.Brains.Routing;
using Aether.AI.Core.Agents;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Skills;
using Aether.AI.Harness.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Aether.AI.Brains.Routing.Tests;

public class BrainRoutingTests
{
    [Fact]
    public async Task Rule_match_does_not_create_llm()
    {
        var llmCreates = 0;
        var client = CreateClient(new ReplyBrain("rule"), () =>
        {
            Interlocked.Increment(ref llmCreates);
            return new ReplyClient("llm");
        });

        Assert.Equal("rule", await Collect(client, "查600000"));
        Assert.Equal(0, llmCreates);
    }

    [Fact]
    public async Task Rule_miss_uses_llm_with_original_context()
    {
        var llm = new RecordingClient("llm");
        var client = CreateClient(new ReplyBrain("rule"), () => llm);

        Assert.Equal("llm", await Collect(client, "普通问题"));
        Assert.Equal("普通问题", llm.LastUserText);
    }

    [Fact]
    public async Task Rule_miss_without_llm_returns_explicit_result()
    {
        var client = CreateClient(new ReplyBrain("rule"), () => null);
        var text = await Collect(client, "普通问题");
        Assert.Contains("未配置 LLM", text);
    }

    [Fact]
    public void Routing_matcher_bounds_pathological_regex_evaluation()
    {
        var definition = new AgentDefinition
        {
            Id = "route-timeout",
            Rules = new RuleProgram { Capture = "^(a+)+$" },
        };
        var input = new ChatMessage(ChatRole.User, new string('a', 100_000) + "!");

        var result = new RuleProgramMatcher("route-timeout").Match(definition, input);

        Assert.Equal(BrainMatchKind.NotMatched, result.Kind);
        Assert.True(result.Reason?.Contains("timed out", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Higher_priority_matcher_wins_without_evaluating_lower_priority()
    {
        var calls = new List<string>();
        var client = new RuleFirstRoutingChatClient(
            new AgentDefinition { Id = "priority" },
            new IBrainMatcher[]
            {
                new RecordingMatcher("low", 1, calls, BrainMatchResult.Matched()),
                new RecordingMatcher("high", 10, calls, BrainMatchResult.Matched()),
            },
            new ReplyBrain("rule"),
            () => new ReplyClient("llm"));

        Assert.Equal("rule", await Collect(client, "anything"));
        Assert.Equal(new[] { "high" }, calls);
    }

    [Fact]
    public async Task Approval_resume_stays_with_rule_and_never_creates_llm()
    {
        var llmCreates = 0;
        var tools = new ToolRegistry();
        tools.Register("DeleteFile", "删除文件", "测试",
            _ => AIFunctionFactory.Create((Func<string, string>)(path => $"deleted:{path}"), "DeleteFile"),
            ToolApprovalLevel.Dangerous);
        var definition = new AgentDefinition
        {
            Id = "routed-approval",
            Name = "RoutedApproval",
            Engine = EngineKind.Llm,
            EnableBrainRouting = true,
            RequireToolApproval = true,
            Tools = new[] { "DeleteFile" },
            Rules = new RuleProgram
            {
                Steps = new()
                {
                    new RuleStep
                    {
                        Match = new() { OnUserRegex = "^删(.+)$", MaxToolCalls = 0 },
                        Action = new() { Tool = "DeleteFile", Args = new() { ["path"] = "{user.1}" } },
                    },
                    new RuleStep
                    {
                        Match = new() { MinToolCalls = 1 },
                        Action = new() { ReplyText = "已删除：{result}" },
                    },
                },
            },
        };
        var routing = new RuleFirstRoutingChatClient(
            definition,
            new[] { new RuleProgramMatcher("routed-approval") },
            new Aether.AI.Brains.Rule.RuleProgramBrain(definition.Rules),
            () =>
            {
                Interlocked.Increment(ref llmCreates);
                return new ReplyClient("llm");
            });
        var factory = new AgentFactory(
            new ReplyClientFactory("unused"), tools, new SkillRegistry(),
            routingFactory: new FixedRoutingFactory(routing));
        var agent = factory.Create(definition);
        var session = await agent.CreateSessionAsync();

        ToolApprovalRequestContent? approval = null;
        await foreach (var update in agent.RunStreamingAsync("删/tmp/a.txt", session))
            approval ??= update.Contents.OfType<ToolApprovalRequestContent>().FirstOrDefault();
        Assert.NotNull(approval);

        var responseMessage = new ChatMessage { Role = ChatRole.User };
        responseMessage.Contents.Add((AIContent)approval!.CreateResponse(approved: true, reason: null));
        var finalText = "";
        await foreach (var update in agent.RunStreamingAsync(new[] { responseMessage }, session))
            finalText += update.Text;

        Assert.Contains("已删除：deleted:/tmp/a.txt", finalText);
        Assert.Equal(0, llmCreates);
    }

    [Fact]
    public async Task Rule_failure_does_not_fallback_to_llm()
    {
        var llmCreates = 0;
        var client = CreateClient(new ThrowingBrain(), () =>
        {
            Interlocked.Increment(ref llmCreates);
            return new ReplyClient("llm");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(client, "查600000"));
        Assert.Equal(0, llmCreates);
    }

    [Fact]
    public async Task Rule_tool_result_resumes_rule_without_llm_fallback()
    {
        var llmCreates = 0;
        var rule = new ToolBrain("rule-call", "rule-finished");
        var client = CreateClient(rule, () =>
        {
            Interlocked.Increment(ref llmCreates);
            return new ReplyClient("llm");
        });
        var history = new List<ChatMessage> { new(ChatRole.User, "查600000") };

        await Drain(client, history);
        history.Add(AssistantCall("rule-call"));
        history.Add(ToolResult("rule-call", "ok"));

        Assert.Equal("rule-finished", await Collect(client, history));
        Assert.Equal(2, rule.Decisions);
        Assert.Equal(0, llmCreates);
    }

    [Fact]
    public async Task Llm_tool_result_resumes_llm_without_rechecking_rule()
    {
        var llm = new ToolClient("llm-call", "llm-finished");
        var client = CreateClient(new ReplyBrain("rule"), () => llm);
        var history = new List<ChatMessage> { new(ChatRole.User, "普通问题") };

        await Drain(client, history);
        history.Add(AssistantCall("llm-call"));
        history.Add(ToolResult("llm-call", "ok"));

        Assert.Equal("llm-finished", await Collect(client, history));
        Assert.Equal(2, llm.Decisions);
    }

    private static RuleFirstRoutingChatClient CreateClient(IBrain rule, Func<IChatClient?> llm)
    {
        var definition = new AgentDefinition
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
        return new RuleFirstRoutingChatClient(
            definition, new[] { new RuleProgramMatcher("route") }, rule, llm);
    }

    private static Task<string> Collect(IChatClient client, string text)
        => Collect(client, new[] { new ChatMessage(ChatRole.User, text) });

    private static async Task<string> Collect(IChatClient client, IEnumerable<ChatMessage> history)
    {
        var output = "";
        await foreach (var update in client.GetStreamingResponseAsync(history))
            output += update.Text;
        return output;
    }

    private static async Task Drain(IChatClient client, IEnumerable<ChatMessage> history)
    {
        await foreach (var _ in client.GetStreamingResponseAsync(history)) { }
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

    private sealed class ReplyBrain(string text) : IBrain
    {
        public ValueTask<IReadOnlyList<AIContent>> DecideAsync(BrainContext context, CancellationToken cancellationToken)
            => new(new AIContent[] { new TextContent(text) });
    }

    private sealed class ThrowingBrain : IBrain
    {
        public ValueTask<IReadOnlyList<AIContent>> DecideAsync(BrainContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("rule failed");
    }

    private sealed class RecordingMatcher(
        string id,
        int priority,
        List<string> calls,
        BrainMatchResult result) : IBrainMatcher
    {
        public string Id => id;
        public int Priority => priority;

        public BrainMatchResult Match(AgentDefinition definition, ChatMessage input)
        {
            calls.Add(id);
            return result;
        }
    }

    private sealed class FixedRoutingFactory(IChatClient client) : IBrainRoutingClientFactory
    {
        public IChatClient Create(AgentDefinition definition) => client;
    }

    private sealed class ReplyClientFactory(string text) : Aether.AI.Brains.Abstractions.IChatClientFactory
    {
        public IChatClient Create(string? modelId) => new ReplyClient(text);
    }

    private sealed class ToolBrain(string callId, string finalText) : IBrain
    {
        public int Decisions { get; private set; }

        public ValueTask<IReadOnlyList<AIContent>> DecideAsync(BrainContext context, CancellationToken cancellationToken)
        {
            Decisions++;
            IReadOnlyList<AIContent> contents = context.LatestToolResult is null
                ? new AIContent[] { new FunctionCallContent(callId, "Echo", new Dictionary<string, object?>()) }
                : new AIContent[] { new TextContent(finalText) };
            return new(contents);
        }
    }

    private class ReplyClient(string text) : IChatClient
    {
        public void Dispose() { }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public virtual async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = new List<AIContent> { new TextContent(text) } };
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    private sealed class RecordingClient(string text) : ReplyClient(text)
    {
        public string? LastUserText { get; private set; }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastUserText = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text;
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
                yield return update;
        }
    }

    private sealed class ToolClient(string callId, string finalText) : IChatClient
    {
        public int Decisions { get; private set; }

        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Decisions++;
            await Task.Yield();
            AIContent content = messages.SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>().Any()
                ? new TextContent(finalText)
                : new FunctionCallContent(callId, "Echo", new Dictionary<string, object?>());
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = new List<AIContent> { content },
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }
}
