using Aether.AI.Core.Agents;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Skills;
using Aether.AI.Harness.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Tests;

/// <summary>
/// 验证规则 agent 的工具审批：RequireToolApproval=true 时，Dangerous 工具（DeleteFile）
/// 应被包成 ApprovalRequiredAIFunction → 流冒 ToolApprovalRequestContent → 批准后执行 → 收尾 reply。
/// 重点验证审批恢复轮规则大脑仍能命中收尾步骤（不被 user-role 审批响应消息打乱 LatestUser/计数）。
/// </summary>
public class RuleApprovalTest
{
    [Fact]
    public async Task Rule_dangerous_tool_triggers_approval_then_resumes()
    {
        // 规则：删(.+) → DeleteFile(filePath) → replyText {result}
        var program = new RuleProgram
        {
            NoMatchReply = "无匹配规则",
            MaxSteps = 20,
            Steps = new()
            {
                new RuleStep
                {
                    Name = "删文件",
                    Match = new() { OnUserRegex = "删(.+)", MaxToolCalls = 0 },
                    Action = new() { Tool = "DeleteFile", Args = new() { ["filePath"] = "{user.1}" } },
                },
                new RuleStep
                {
                    Match = new() { MinToolCalls = 1 },
                    Action = new() { ReplyText = "已删除：{result}" },
                },
            },
        };

        var tools = new ToolRegistry();
        tools.Register("DeleteFile", "删除文件", "测试", _ => AIFunctionFactory.Create((Func<string, string>)DeleteFile),
            ToolApprovalLevel.Dangerous);

        var factory = new AgentFactory(new NullChatClientFactory(), tools, new SkillRegistry());
        var agent = factory.Create(new AgentDefinition
        {
            Id = "rule-del",
            Name = "RuleDel",
            Engine = EngineKind.Rule,
            Rules = program,
            Instructions = "rule",
            Tools = new[] { "DeleteFile" },
            Memory = AgentMemoryMode.Off,
            RequireToolApproval = true, // 开审批
        });

        var session = await agent.CreateSessionAsync();

        // 1) 首轮流：应冒出 ToolApprovalRequestContent（DeleteFile 是 Dangerous，被包装）
        ToolApprovalRequestContent? approvalReq = null;
        await foreach (var update in agent.RunStreamingAsync("删/tmp/scratch.txt", session))
        {
            foreach (var c in update.Contents)
            {
                if (c is ToolApprovalRequestContent req) approvalReq = req;
            }
        }
        Assert.NotNull(approvalReq);
        Assert.Equal("DeleteFile", ((FunctionCallContent)approvalReq!.ToolCall).Name);

        // 2) 批准后二次调用：应执行工具 → FunctionResultContent → 收尾 reply
        var response = approvalReq.CreateResponse(approved: true, reason: null);
        var responseMsg = new ChatMessage { Role = ChatRole.User };
        responseMsg.Contents.Add((AIContent)response);

        bool sawResult = false;
        var finalText = "";
        await foreach (var update in agent.RunStreamingAsync(new[] { responseMsg }, session))
        {
            foreach (var c in update.Contents)
            {
                if (c is FunctionResultContent) sawResult = true;
            }
            if (!string.IsNullOrEmpty(update.Text)) finalText += update.Text;
        }
        Console.WriteLine($"SAW_RESULT={sawResult} FINAL_TEXT={finalText}");
        Assert.True(sawResult, "批准后 DeleteFile 应被执行并回填结果");
        Assert.NotEqual("无匹配规则", finalText);
        Assert.Contains("已删除：deleted:/tmp/scratch.txt", finalText);
    }

    private static string DeleteFile(string filePath) => $"deleted:{filePath}";

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
