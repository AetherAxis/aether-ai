using System.Runtime.CompilerServices;
using Aether.AI.Core.Agents;
using Aether.AI.Harness.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Tests;

/// <summary>
/// Rule Agent 接法 C（规则当工具）测试：<see cref="RuleToolExecutor"/> 自包含循环单测 +
/// <see cref="RuleTool"/> 包成 <c>run_rule</c> <see cref="AIFunction"/> 单测 + 端到端（模拟 LLM agent
/// 调 run_rule → executor 内部跑工具链 → 压缩结果回填,业务工具对 LLM 隐藏）。
/// </summary>
public class RuleToolTests
{
    // —— RuleToolExecutor 自包含循环单测 ——

    [Fact]
    public async Task Executor_runs_tool_chain_and_returns_final_text()
    {
        var program = new RuleProgram
        {
            Steps = new()
            {
                new RuleStep
                {
                    Match = new() { OnUserRegex = @"查(?<sym>\d+)", MaxToolCalls = 0 },
                    Action = new() { Tool = "Echo", Args = new() { ["text"] = "{user.1}" } },
                },
                new RuleStep { Match = new() { MinToolCalls = 1 }, Action = new() { ReplyText = "结果：{result}" } },
            },
        };
        var echoCalls = 0;
        AIFunction? Resolver(string n) => n == "Echo"
            ? AIFunctionFactory.Create((Func<string, string>)(text => { echoCalls++; return "echo:" + text; }), "Echo")
            : null;

        var exec = new RuleToolExecutor(new RuleProgramBrain(program), Resolver);
        var result = await exec.RunAsync("查600000");

        Assert.Equal("结果：echo:600000", result);
        Assert.Equal(1, echoCalls); // Echo 被 executor 内部调用一次
    }

    [Fact]
    public async Task Executor_chains_multiple_tools_invisibly()
    {
        // step0: Append(user 捕获) ; step1: Append({result}) ; step2: reply {result}
        // 两次工具调用都在 executor 内部完成，调用方只拿到最终文本。
        var program = new RuleProgram
        {
            Steps = new()
            {
                new RuleStep { Match = new() { OnUserRegex = "(.+)", MaxToolCalls = 0 },
                               Action = new() { Tool = "Append", Args = new() { ["text"] = "{user.1}" } } },
                new RuleStep { Match = new() { MinToolCalls = 1, MaxToolCalls = 1 },
                               Action = new() { Tool = "Append", Args = new() { ["text"] = "{result}" } } },
                new RuleStep { Match = new() { MinToolCalls = 2 }, Action = new() { ReplyText = "{result}" } },
            },
        };
        var calls = 0;
        AIFunction? Resolver(string n) => n == "Append"
            ? AIFunctionFactory.Create((Func<string, string>)(text => { calls++; return text + "+X"; }), "Append")
            : null;

        var exec = new RuleToolExecutor(new RuleProgramBrain(program), Resolver);
        var result = await exec.RunAsync("a");

        Assert.Equal("a+X+X", result);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Executor_no_match_returns_default_reply()
    {
        var program = new RuleProgram
        {
            NoMatchReply = "听不懂",
            Steps = new() { new RuleStep { Match = new() { OnUserRegex = "^x$" }, Action = new() { ReplyText = "hit" } } },
        };
        var exec = new RuleToolExecutor(new RuleProgramBrain(program), _ => null);

        Assert.Equal("听不懂", await exec.RunAsync("y"));
    }

    [Fact]
    public async Task Executor_missing_tool_degrades_gracefully()
    {
        // 规则要调 Ghost，但 resolver 没有它 → executor 回填「工具不存在」→ 下一步把它当 result 终答。
        var program = new RuleProgram
        {
            Steps = new()
            {
                new RuleStep { Match = new() { MaxToolCalls = 0 }, Action = new() { Tool = "Ghost" } },
                new RuleStep { Match = new() { MinToolCalls = 1 }, Action = new() { ReplyText = "结果：{result}" } },
            },
        };
        var exec = new RuleToolExecutor(new RuleProgramBrain(program), _ => null);

        var result = await exec.RunAsync("hi");
        Assert.Contains("工具不存在", result);
    }

    // —— RuleTool（AIFunction facade）单测 ——

    [Fact]
    public async Task RuleTool_wraps_registry_program_as_aifunction()
    {
        var program = new RuleProgram
        {
            Steps = new()
            {
                new RuleStep
                {
                    Match = new() { OnUserRegex = @"查(?<sym>\d+)", MaxToolCalls = 0 },
                    Action = new() { Tool = "Echo", Args = new() { ["text"] = "{user.1}" } },
                },
                new RuleStep { Match = new() { MinToolCalls = 1 }, Action = new() { ReplyText = "echo:{result}" } },
            },
        };
        var reg = new RuleRegistry();
        reg.Register("echo_rule", program, "回显查询");

        var runRule = RuleTool.Create(reg, n => n == "Echo"
            ? AIFunctionFactory.Create((Func<string, string>)(text => "echo:" + text), "Echo")
            : null);

        Assert.Equal("run_rule", runRule.Name);
        Assert.Contains("echo_rule", runRule.Description); // 描述枚举了已注册规则

        var resultObj = await runRule.InvokeAsync(
            new AIFunctionArguments { ["name"] = "echo_rule", ["input"] = "查600000" });
        Assert.Equal("echo:echo:600000", resultObj?.ToString());
    }

    [Fact]
    public async Task RuleTool_unknown_rule_returns_message()
    {
        var runRule = RuleTool.Create(new RuleRegistry(), _ => null);
        var resultObj = await runRule.InvokeAsync(
            new AIFunctionArguments { ["name"] = "nope", ["input"] = "x" });
        Assert.Contains("规则不存在", resultObj?.ToString());
    }

    [Fact]
    public async Task RuleTool_injects_runtime_cancellation_token_into_inner_tools()
    {
        // 规则：任何输入 → Abort。Abort 检查它拿到的 CT——若 RuleTool 的 invoke 仍传
        // CancellationToken.None（修复前），agent 中断/超时后规则链内部业务工具照跑。
        var program = new RuleProgram
        {
            Steps = new()
            {
                new RuleStep { Match = new() { MaxToolCalls = 0 }, Action = new() { Tool = "Abort", Args = new() { ["input"] = "x" } } },
                new RuleStep { Match = new() { MinToolCalls = 1 }, Action = new() { ReplyText = "ok" } },
            },
        };
        var reg = new RuleRegistry();
        reg.Register("abort_rule", program, "取消检查");
        var sawCanceledCt = false;
        var runRule = RuleTool.Create(reg, n => n == "Abort"
            // 内层工具也要用惯例名 cancellationToken —— MEAI 按「参名==cancellationToken」识别注入参数、
            // 排除出 JSON schema（不按类型）：旧写法 (_, ct) 让 ct 成了普通 schema 参，取不到运行时 CT。
            ? AIFunctionFactory.Create((Func<string, CancellationToken, string>)((input, cancellationToken) =>
              {
                  sawCanceledCt = cancellationToken.IsCancellationRequested;
                  return "aborted";
              }), "Abort")
            : null);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // 模拟 agent 运行已在取消

        // 调用方 CT 经 AIFunctionFactory 注入 invoke 的 CancellationToken 末参（MEAI 约定），
        // 再透传给 executor 内部业务工具。旧实现此处是 CancellationToken.None → 断言失败。
        await runRule.InvokeAsync(
            new AIFunctionArguments { ["name"] = "abort_rule", ["input"] = "x" }, cts.Token);

        Assert.True(sawCanceledCt); // 内部工具收到的 CT == 调用 run_rule 的 CT（非 None）
    }

    // —— 端到端：模拟 LLM agent 调 run_rule,业务工具对 LLM 隐藏 ——

    [Fact]
    public async Task RuleTool_runs_inside_agent_loop_hidden_from_outer()
    {
        // 规则：查(\d+) → Echo → reply "echo:{result}"
        var program = new RuleProgram
        {
            Steps = new()
            {
                new RuleStep
                {
                    Match = new() { OnUserRegex = @"查(?<sym>\d+)", MaxToolCalls = 0 },
                    Action = new() { Tool = "Echo", Args = new() { ["text"] = "{user.1}" } },
                },
                new RuleStep { Match = new() { MinToolCalls = 1 }, Action = new() { ReplyText = "echo:{result}" } },
            },
        };
        var reg = new RuleRegistry();
        reg.Register("echo_rule", program, "回显查询");

        var echoCalls = 0;
        var runRule = RuleTool.Create(reg, n => n == "Echo"
            ? AIFunctionFactory.Create((Func<string, string>)(text => { echoCalls++; return "echo:" + text; }), "Echo")
            : null);

        // 模拟 LLM：首次调 run_rule，第二次给终答。
        var client = new ScriptedChatClient();
        client.Script.Add(new ChatResponseUpdate
        {
            Contents = new AIContent[]
            {
                new FunctionCallContent("c1", "run_rule",
                    new Dictionary<string, object?> { ["name"] = "echo_rule", ["input"] = "查600000" }),
            },
        });
        client.Script.Add(new ChatResponseUpdate { Contents = new AIContent[] { new TextContent("done") } });

        // 关键：agent 的工具池只有 run_rule，Echo 不在里面（对 LLM 隐藏）。
        var options = new HarnessAgentOptions
        {
            Id = "rule-tool-spike",
            Name = "RuleToolSpike",
            Description = "spike",
            DisableTodoProvider = true,
            ChatOptions = new ChatOptions { Instructions = "use run_rule", Tools = new List<AITool> { runRule } },
        };
        var agent = client.AsHarnessAgent(options);
        var session = await agent.CreateSessionAsync();

        string? ruleResult = null;
        var sawEchoDirectly = false;
        var finalText = "";
        await foreach (var update in agent.RunStreamingAsync("查600000", session))
        {
            foreach (var c in update.Contents)
            {
                // FunctionResultContent 无 Name 属性（仅 CallId/Result）；agent 只调了 run_rule，
                // 故流里唯一的 FunctionResultContent 即 run_rule 的回填结果。
                if (c is FunctionResultContent frc)
                {
                    ruleResult ??= frc.Result?.ToString();
                }
                if (c is FunctionCallContent fcc && fcc.Name == "Echo")
                {
                    sawEchoDirectly = true; // 不应发生：Echo 对外层 LLM 隐藏
                }
            }
            if (!string.IsNullOrEmpty(update.Text))
            {
                finalText += update.Text;
            }
        }

        Assert.Equal("echo:echo:600000", ruleResult); // run_rule 返回的压缩结果回填给 LLM
        Assert.Equal(1, echoCalls);                    // Echo 被 executor 内部调了一次
        Assert.False(sawEchoDirectly);                 // 外层 agent 从未直接调 Echo
        Assert.Equal("done", finalText);               // 外层 agent 终答
    }

    /// <summary>按脚本逐条返回流式更新的伪 IChatClient（镜像 ToolApprovalSpikeTests 的同名助手）。</summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        public List<ChatResponseUpdate> Script { get; } = new();
        private int _calls;

        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var index = _calls++;
            return Stream(index, cancellationToken);
        }

        private async IAsyncEnumerable<ChatResponseUpdate> Stream(int index, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            if (index >= 0 && index < Script.Count) // Script 在调用前已填好
            {
                yield return Script[index];
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }
}
