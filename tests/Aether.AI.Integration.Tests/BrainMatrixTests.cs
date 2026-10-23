using Aether.AI.Brains.Abstractions;
using Aether.AI.Brains.Code;
using Aether.AI.Brains.Code.CSharp;
using Aether.AI.Brains.Llm;
using Aether.AI.Brains.Rule;
using Aether.AI.Brains.Rule.NRules;
using Aether.AI.Brains.Rule.RulesEngine;
using Aether.AI.Core.Agents;
using Aether.AI.Harness.Agents;
using Aether.AI.Hosting;
using Aether.AI.Integration.Tests.Support;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.AI.Integration.Tests;

/// <summary>
/// 五种大脑实现在<b>同一个宿主</b>里共存、可切换：同一输入、同一工具、同一期望输出。
/// 这是跨包属性，单包测试看不到（每个单包测试只引自己的大脑包）。
/// LLM 用脚本化替身（无 key），验证的是接缝接线而非模型质量。
/// </summary>
public class BrainMatrixTests
{
    [Fact]
    public async Task All_five_brain_kinds_run_in_one_host()
    {
        var codeBrains = new CodeBrainRegistry();
        codeBrains.Register("code-matrix", static () => new EchoCodeBrain());

        var services = new ServiceCollection();
        services.AddSingleton<ICodeBrainRegistry>(codeBrains);
        services.AddRuleBrains();             // program
        services.AddRulesEngineRuleBrains();  // rulesEngine
        services.AddNRulesRuleBrains();       // nRules
        services.AddCSharpCodeBrains();       // Code（预编译）
        services.AddLlmBrainFactory();        // LLM（同一 IBrainFactory 接缝）
        services.AddSingleton<IChatClientFactory>(new ScriptedChatClientFactory());
        services.AddAetherAI()
            .AddTool((Func<string, string>)(text => $"echo:{text}"), "Echo");

        using var provider = services.BuildServiceProvider();
        var agents = provider.GetRequiredService<AgentFactory>();

        await AssertRunsAsync(agents, "program", ProgramAgent());
        await AssertRunsAsync(agents, "rulesEngine", RulesEngineAgent());
        await AssertRunsAsync(agents, "nRules", NRulesAgent());
        await AssertRunsAsync(agents, "code(precompiled)", CodeAgent());
        await AssertRunsAsync(agents, "llm(scripted)", LlmAgent());
    }

    private static async Task AssertRunsAsync(AgentFactory agents, string label, AgentDefinition definition)
    {
        var agent = agents.Create(definition);
        var session = await agent.CreateSessionAsync();
        var output = string.Empty;
        await foreach (var update in agent.RunStreamingAsync("查600000", session))
            output += update.Text;

        // 五条链路都必须「调用 Echo → 回填结果 → 终答」，输出完全一致。
        Assert.True(output == "done:echo:600000", $"{label}: unexpected output '{output}'");
    }

    // —— 五份定义：同一输入、同一工具、同一期望输出 ——

    private static AgentDefinition ProgramAgent() => new AgentBuilder("program-matrix")
        .Name("Program matrix")
        .Engine(EngineKind.Rule)
        .BrainBackend("program")
        .Memory(AgentMemoryMode.Off)
        .WithTool("Echo")
        .Rules(new RuleProgram
        {
            Steps = new()
            {
                new RuleStep
                {
                    Match = new() { OnUserRegex = "^查(.+)$", MaxToolCalls = 0 },
                    Action = new() { Tool = "Echo", Args = new() { ["text"] = "{user.1}" } },
                },
                new RuleStep
                {
                    Match = new() { MinToolCalls = 1 },
                    Action = new() { ReplyText = "done:{result}" },
                },
            },
        })
        .Build();

    private static AgentDefinition RulesEngineAgent() => new AgentBuilder("rulesengine-matrix")
        .Name("RulesEngine matrix")
        .Engine(EngineKind.Rule)
        .BrainBackend("rulesEngine")
        .Memory(AgentMemoryMode.Off)
        .WithTool("Echo")
        .RuleSource("""
            [{
              "WorkflowName": "matrix",
              "Rules": [
                {
                  "RuleName": "query",
                  "Expression": "brain.UserText.StartsWith(\"查\") && brain.ToolCallCount == 0",
                  "Properties": { "tool": "Echo", "capture": "查(?<sym>.+)", "args": { "text": "{user.1}" } }
                },
                {
                  "RuleName": "reply",
                  "Expression": "brain.ToolCallCount == 1 && brain.LastResult != null",
                  "Properties": { "reply": "done:{result}" }
                }
              ]
            }]
            """)
        .Build();

    private static AgentDefinition NRulesAgent() => new AgentBuilder("nrules-matrix")
        .Name("NRules matrix")
        .Engine(EngineKind.Rule)
        .BrainBackend("nRules")
        .Memory(AgentMemoryMode.Off)
        .WithTool("Echo")
        .RuleSource("""
            {
              "ruleTypes": [
                "Aether.AI.Integration.Tests.Support.MatrixQueryRule",
                "Aether.AI.Integration.Tests.Support.MatrixReplyRule"
              ]
            }
            """)
        .Build();

    private static AgentDefinition CodeAgent() => new AgentBuilder("code-matrix")
        .Name("Code matrix")
        .Engine(EngineKind.Code)
        .Memory(AgentMemoryMode.Off)
        .WithTool("Echo")
        .Build();

    private static AgentDefinition LlmAgent() => new AgentBuilder("llm-matrix")
        .Name("LLM matrix")
        .Engine(EngineKind.Llm)
        .ModelId("scripted")
        .Memory(AgentMemoryMode.Off)
        .WithTool("Echo")
        .Build();
}
