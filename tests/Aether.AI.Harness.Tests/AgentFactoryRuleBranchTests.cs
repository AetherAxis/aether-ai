using Aether.AI.Core.Agents;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Skills;
using Aether.AI.Harness.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Tests;

/// <summary>
/// Rule Agent：验证 <see cref="AgentFactory.Create"/> 的规则分支端到端——
/// <see cref="AgentDefinition.Engine"/>==<see cref="EngineKind.Rule"/> 时早返 <c>BuildRuleAgent</c>，
/// 规则大脑驱动 MAF HarnessAgent 工具循环（无 LLM），终答含工具结果。规则分支复用
/// <see cref="AgentDefinition.Tools"/>（编辑器勾选的工具 + ask_user），与 LLM agent 一致。
/// </summary>
public class AgentFactoryRuleBranchTests
{
    [Fact]
    public async Task Create_rule_branch_runs_program_without_llm()
    {
        // 规则：查(\d+) → 调 Echo 传 {user.1}；调过一次后 → 终答「结果：{result}」。
        var program = new RuleProgram
        {
            Steps = new()
            {
                new RuleStep
                {
                    Match = new() { OnUserRegex = @"查(?<sym>\d+)", MinToolCalls = 0, MaxToolCalls = 0 },
                    Action = new() { Tool = "Echo", Args = new() { ["text"] = "{user.1}" } },
                },
                new RuleStep
                {
                    Match = new() { MinToolCalls = 1 },
                    Action = new() { ReplyText = "结果：{result}" },
                },
            },
        };

        var tools = new ToolRegistry();
        tools.Register("Echo", "回显输入", "测试", _ => AIFunctionFactory.Create((Func<string, string>)Echo));

        // 规则分支不用 IChatClientFactory（不建 LLM 客户端），NullChatClientFactory 仅满足构造。
        var factory = new AgentFactory(new NullChatClientFactory(), tools, new SkillRegistry());
        var agent = factory.Create(new AgentDefinition
        {
            Id = "rule-af",
            Name = "RuleAF",
            Engine = EngineKind.Rule,
            Rules = program,
            Instructions = "rule",
            Tools = new[] { "Echo" },
        });

        var session = await agent.CreateSessionAsync();
        var finalText = "";
        var sawResult = false;
        await foreach (var update in agent.RunStreamingAsync("查600000", session))
        {
            if (update.Contents.Any(c => c is FunctionResultContent)) sawResult = true;
            if (!string.IsNullOrEmpty(update.Text)) finalText += update.Text;
        }

        // 证明：早返 + BuildRuleAgent + 关 TodoProvider 全通；工具被实执行、结果回填、模板替换。
        Assert.True(sawResult, "Echo 应被 MAF 执行并回填 FunctionResultContent");
        Assert.Equal("结果：echo:600000", finalText);
    }

    private static string Echo(string text) => $"echo:{text}";

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
