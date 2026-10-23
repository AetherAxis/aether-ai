using System.Collections;
using Aether.AI.Core.Agents;
using Aether.AI.Harness.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Tests;

/// <summary>
/// Rule Agent 1.0 刺探测试：验证「规则大脑代替 LLM、复用 MAF HarnessAgent 生态」的接缝。
/// 分两类：(1) <see cref="RuleProgramBrain"/> 纯单测——喂 <see cref="RuleContext"/> 断言流出的
/// <see cref="AIContent"/>；(2) <see cref="RuleChatClient"/> + <c>AsHarnessAgent</c> 端到端——
/// 证明规则 client 驱动 MAF agent 循环（工具执行 → 结果回填 → 终答终止），无 LLM。
/// </summary>
public class RuleAgentTests
{
    // —— RuleProgramBrain 单测 ——

    [Fact]
    public async Task Router_match_emits_tool_call()
    {
        var program = new RuleProgram
        {
            Steps = new()
            {
                new RuleStep
                {
                    Match = new() { OnUserRegex = @"查(?<sym>\d+)" },
                    Action = new() { Tool = "Query", Args = new() { ["symbol"] = "{user.1}" } },
                },
            },
        };
        var brain = new RuleProgramBrain(program);

        var ctx = new RuleContext { LatestUser = User("查600000"), ToolCallsThisTurn = 0 };
        var output = await brain.DecideAsync(ctx, default);

        var fc = Assert.Single(output.OfType<FunctionCallContent>());
        Assert.Equal("Query", fc.Name);
        var args = Assert.IsAssignableFrom<IDictionary<string, object?>>(fc.Arguments);
        Assert.Equal("600000", args["symbol"]?.ToString());
    }

    [Fact]
    public async Task Step_advance_replies_after_tool()
    {
        // step0 仅在 0 次调用时调工具；step1 在 ≥1 次调用时给终答。
        var program = new RuleProgram
        {
            Steps = new()
            {
                new RuleStep { Match = new() { MinToolCalls = 0, MaxToolCalls = 0 }, Action = new() { Tool = "Do" } },
                new RuleStep { Match = new() { MinToolCalls = 1 }, Action = new() { ReplyText = "done" } },
            },
        };
        var brain = new RuleProgramBrain(program);

        // 首次（0 次工具）→ 工具调用
        var first = await brain.DecideAsync(new RuleContext { ToolCallsThisTurn = 0 }, default);
        Assert.Single(first.OfType<FunctionCallContent>());

        // 再次（1 次工具）→ 终答
        var second = await brain.DecideAsync(new RuleContext { ToolCallsThisTurn = 1 }, default);
        var text = Assert.Single(second.OfType<TextContent>());
        Assert.Equal("done", text.Text);
    }

    [Fact]
    public async Task NoMatch_terminates_with_default_reply()
    {
        var program = new RuleProgram
        {
            Steps = new() { new RuleStep { Match = new() { OnUserRegex = "^x$" }, Action = new() { ReplyText = "hit" } } },
        };
        var brain = new RuleProgramBrain(program);

        var output = await brain.DecideAsync(new RuleContext { LatestUser = User("y") }, default);

        var text = Assert.Single(output.OfType<TextContent>());
        Assert.Equal("无匹配规则", text.Text);
    }

    [Fact]
    public async Task NoMatch_terminates_with_custom_reply()
    {
        var program = new RuleProgram
        {
            NoMatchReply = "听不懂",
            Steps = new() { new RuleStep { Match = new() { OnUserRegex = "^x$" }, Action = new() { ReplyText = "hit" } } },
        };
        var brain = new RuleProgramBrain(program);

        var output = await brain.DecideAsync(new RuleContext { LatestUser = User("y") }, default);

        Assert.Equal("听不懂", Assert.Single(output.OfType<TextContent>()).Text);
    }

    [Fact]
    public async Task MaxSteps_forces_termination()
    {
        var program = new RuleProgram
        {
            MaxSteps = 2,
            Steps = new() { new RuleStep { Match = new(), Action = new() { Tool = "Loop" } } }, // 永远调工具
        };
        var brain = new RuleProgramBrain(program);

        var output = await brain.DecideAsync(new RuleContext { ToolCallsThisTurn = 2 }, default);

        Assert.Contains("超限", Assert.Single(output.OfType<TextContent>()).Text);
    }

    [Fact]
    public async Task OnResultRegex_branches_on_prior_tool_result()
    {
        var program = new RuleProgram
        {
            Steps = new()
            {
                new RuleStep { Match = new() { OnResultRegex = "ERROR" }, Action = new() { ReplyText = "出错啦" } },
                new RuleStep { Match = new(), Action = new() { ReplyText = "ok" } },
            },
        };
        var brain = new RuleProgramBrain(program);

        var errOut = await brain.DecideAsync(
            new RuleContext { LatestToolResult = new FunctionResultContent("c1", "ERROR: timeout") }, default);
        Assert.Equal("出错啦", Assert.Single(errOut.OfType<TextContent>()).Text);

        var okOut = await brain.DecideAsync(
            new RuleContext { LatestToolResult = new FunctionResultContent("c2", "200 OK") }, default);
        Assert.Equal("ok", Assert.Single(okOut.OfType<TextContent>()).Text);
    }

    [Fact]
    public async Task ReplyText_substitutes_templates()
    {
        var program = new RuleProgram
        {
            Steps = new()
            {
                new RuleStep
                {
                    Match = new() { OnUserRegex = @"(?<w>\w+)" },
                    Action = new() { ReplyText = "你说:{user} 词:{user.1} 果:{result}" },
                },
            },
        };
        var brain = new RuleProgramBrain(program);

        var output = await brain.DecideAsync(
            new RuleContext { LatestUser = User("hello"), LatestToolResult = new FunctionResultContent("c", "R") }, default);

        Assert.Equal("你说:hello 词:hello 果:R", Assert.Single(output.OfType<TextContent>()).Text);
    }

    [Fact]
    public async Task Program_capture_supplies_groups_to_step_without_onUserRegex()
    {
        // 程序级 capture：从 user 消息提取参数；步骤本身无 OnUserRegex（靠 minToolCalls/maxToolCalls 命中），
        // 仍能用 {user.1} 引用 capture 捕获组。典型用法：多步复用同一参数免重复正则。
        var program = new RuleProgram
        {
            Capture = "读(.+)",
            Steps = new()
            {
                new RuleStep { Match = new() { MaxToolCalls = 0 },
                               Action = new() { Tool = "Read", Args = new() { ["filePath"] = "{user.1}" } } },
                new RuleStep { Match = new() { MinToolCalls = 1 }, Action = new() { ReplyText = "内容：{result}" } },
            },
        };
        var brain = new RuleProgramBrain(program);

        var first = await brain.DecideAsync(new RuleContext { LatestUser = User("读/tmp/note.txt"), ToolCallsThisTurn = 0 }, default);
        var fc = Assert.Single(first.OfType<FunctionCallContent>());
        Assert.Equal("Read", fc.Name);
        var args = Assert.IsAssignableFrom<IDictionary<string, object?>>(fc.Arguments);
        Assert.Equal("/tmp/note.txt", args["filePath"]?.ToString());
    }

    [Fact]
    public async Task Capture_gates_when_user_input_does_not_match()
    {
        // 程序级 capture 设定但 user 输入不匹配 → 不让无 OnUserRegex 的步骤兜底命中，直接 no-match。
        // 否则「你是谁」会错误命中 maxToolCalls:0 的 Read 步骤（capture 只供参不拦截的 footgun）。
        var program = new RuleProgram
        {
            Capture = "读(.+)",
            NoMatchReply = "听不懂",
            Steps = new()
            {
                new RuleStep { Match = new() { MaxToolCalls = 0 },
                               Action = new() { Tool = "Read", Args = new() { ["filePath"] = "{user.1}" } } },
            },
        };
        var brain = new RuleProgramBrain(program);

        var output = await brain.DecideAsync(new RuleContext { LatestUser = User("你是谁"), ToolCallsThisTurn = 0 }, default);

        Assert.Empty(output.OfType<FunctionCallContent>()); // 不调工具
        Assert.Equal("听不懂", Assert.Single(output.OfType<TextContent>()).Text); // 走 no-match
    }

    // —— RuleChatClient 端到端刺探（驱动真实 MAF agent 循环，无 LLM）——

    [Fact]
    public async Task RuleChatClient_direct_emits_tool_call_for_user_message()
    {
        // 绕过 MAF agent，直接喂 history 给 RuleChatClient，隔离 BuildContext 是否正确识别 user 消息。
        var program = new RuleProgram
        {
            Steps = new()
            {
                new RuleStep { Match = new() { OnUserRegex = @"查(?<sym>\d+)" },
                               Action = new() { Tool = "Query", Args = new() { ["symbol"] = "{user.1}" } } },
            },
        };
        var client = new RuleChatClient(new RuleProgramBrain(program));
        var history = new List<ChatMessage> { new(ChatRole.User, "查600000") };

        ChatResponseUpdate? update = null;
        await foreach (var u in client.GetStreamingResponseAsync(history)) update = u;

        Assert.NotNull(update);
        Assert.Contains(update!.Contents, c => c is FunctionCallContent);
    }

    [Fact]
    public async Task RuleChatClient_keeps_genuine_user_msg_across_tool_result_in_chain()
    {
        // 回归守卫：工具结果也是 ChatRole.User（装 FunctionResultContent，无 TextContent）。
        // BuildContext 扫「最后一条 user」必须跳过工具结果消息取到真正用户输入——否则 userText=""
        // → 程序级 Capture 失配、toolCallsThisTurn 归零、OnUserRegex 失配（工具链中段断链）。
        // 镜像 SlidingWindowChatReducer「安全 user 起点 = user 角色 + 不含 FunctionResultContent」。
        var program = new RuleProgram
        {
            Capture = "查(?<sym>\\d+)",
            Steps = new()
            {
                // 步骤 0：首轮命中 → 调 Echo 传 {user.1}（首轮 toolCallsThisTurn=0≤MaxToolCalls=0）。
                new RuleStep { Match = new() { OnUserRegex = "查(?<sym>\\d+)", MaxToolCalls = 0 },
                               Action = new() { Tool = "Echo", Args = new() { ["text"] = "{user.1}" } } },
                // 步骤 1：工具已调一次后 → 终答引用 {user.1}（证明 LatestUser 仍指向真正 user，Capture 未丢）。
                new RuleStep { Match = new() { MinToolCalls = 1 },
                               Action = new() { ReplyText = "结果：{user.1}" } },
            },
        };
        var client = new RuleChatClient(new RuleProgramBrain(program));

        // 工具链中段历史：真正 user 输入 → assistant 工具调用 → user 工具结果（FunctionResultContent）。
        var call = new FunctionCallContent("c1", "Echo", new Dictionary<string, object?> { ["text"] = "600000" });
        var callMsg = new ChatMessage { Role = ChatRole.Assistant };
        callMsg.Contents.Add(call);
        var resultMsg = new ChatMessage { Role = ChatRole.User };
        resultMsg.Contents.Add(new FunctionResultContent("c1", "echo:600000"));
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "查600000"),
            callMsg,
            resultMsg,
        };

        var text = "";
        await foreach (var u in client.GetStreamingResponseAsync(history)) text += u.Text;

        // {user.1}="600000" 来自真正 user 消息的 Capture——证明 LatestUser 指向真正 user 而非工具结果。
        // 修复前会输出「无匹配规则」（userText 空 → Capture 失配 + toolCallsThisTurn=0 → 两条步骤都不命中）。
        Assert.Equal("结果：600000", text);
    }

    [Fact]
    public async Task RuleChatClient_drives_agent_loop_tool_then_reply()
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

        var echo = AIFunctionFactory.Create((Func<string, string>)Echo);
        var client = new RuleChatClient(new RuleProgramBrain(program));
        var options = new HarnessAgentOptions
        {
            Id = "rule-spike",
            Name = "RuleSpike",
            Description = "rule agent spike",
            // 规则大脑不消费注入式 NL 上下文（todo/mode 是给 LLM 读的）——关掉 TodoProvider，
            // 否则它会在真实 user 消息后追加一条「### Current todo list」user 消息，污染 LatestUser。
            DisableTodoProvider = true,
            ChatOptions = new ChatOptions { Instructions = "rule", Tools = new List<AITool> { echo } },
        };
        var agent = client.AsHarnessAgent(options);
        var session = await agent.CreateSessionAsync();

        bool sawResult = false;
        var finalText = "";
        await foreach (var update in agent.RunStreamingAsync("查600000", session))
        {
            foreach (var c in update.Contents)
            {
                if (c is FunctionResultContent) sawResult = true;
            }
            if (!string.IsNullOrEmpty(update.Text)) finalText += update.Text;
        }

        // MAF HarnessAgent 内部消费 FunctionCallContent、执行工具、回填 FunctionResultContent，
        // 再调规则大脑拿终答。终答含「echo:600000」= 工具被实执行 + 结果回填 + 模板替换全通。
        Assert.True(sawResult, "MAF 应执行 Echo 并回填 FunctionResultContent");
        Assert.Equal("结果：echo:600000", finalText);
    }

    private static ChatMessage User(string text) => new(ChatRole.User, text);

    private static string Echo(string text) => $"echo:{text}";
}
