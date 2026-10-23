using Aether.AI.Core.Agents;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Skills;
using Aether.AI.Harness.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Tests;

/// <summary>
/// 回归：规则 agent 显式 <c>Memory=Off</c> 时，工具调用后仍能命中收尾 reply 步骤（不误报「无匹配规则」）。
/// 根因：NoMemoryChatHistoryProvider 连轮内 history 也清掉 → 工具后 MAF re-invoke 只传 [tool:result]，
/// LatestUser=null + ToolCallsThisTurn=0 → NoMatch。修复：BuildRuleAgent 遇 NoMemory 退回框架默认（InMemory）。
/// 复现用户「列&lt;路径&gt; → ExploreDirectory → replyText {result}」规则。
/// </summary>
public class ReproListDirTest
{
    [Fact]
    public async Task Rule_with_memory_off_still_replies_after_tool()
    {
        var program = new RuleProgram
        {
            NoMatchReply = "无匹配规则",
            MaxSteps = 20,
            Steps = new()
            {
                new RuleStep
                {
                    Name = "列目录",
                    Match = new() { OnUserRegex = "列(.+)", MaxToolCalls = 0 },
                    Action = new() { Tool = "ExploreDirectory", Args = new() { ["path"] = "{user.1}", ["recursionDepth"] = 1 } },
                },
                new RuleStep
                {
                    Match = new() { MinToolCalls = 1 },
                    Action = new() { ReplyText = "{result}" },
                },
            },
        };

        var tools = new ToolRegistry();
        tools.Register("ExploreDirectory", "列目录", "测试", _ => AIFunctionFactory.Create((Func<string, int, string>)ExploreDirectory));

        var factory = new AgentFactory(new NullChatClientFactory(), tools, new SkillRegistry());
        var agent = factory.Create(new AgentDefinition
        {
            Id = "rule-list",
            Name = "RuleList",
            Engine = EngineKind.Rule,
            Rules = program,
            Instructions = "rule",
            Tools = new[] { "ExploreDirectory" },
            Memory = AgentMemoryMode.Off, // 用户规则显式 Off
        });

        var session = await agent.CreateSessionAsync();
        var finalText = "";
        var sawResult = false;
        await foreach (var update in agent.RunStreamingAsync("列/Users/360jr/Desktop", session))
        {
            if (update.Contents.Any(c => c is FunctionResultContent)) sawResult = true;
            if (!string.IsNullOrEmpty(update.Text)) finalText += update.Text;
        }

        Assert.True(sawResult, "ExploreDirectory 应被 MAF 执行并回填 FunctionResultContent");
        Assert.NotEqual("无匹配规则", finalText);
        Assert.Contains("listed:/Users/360jr/Desktop:d1", finalText);
    }

    private static string ExploreDirectory(string path, int recursionDepth)
        => $"listed:{path}:d{recursionDepth}";

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
