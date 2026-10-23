using Aether.AI.BrainMatrix.Consumer;
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
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

// 一个宿主同时注册全部大脑实现，逐个真实跑通「用户输入 → 大脑决策 → 工具执行 → 回填 → 终答」。
// LLM 用脚本化 IChatClient 代替真实模型（本机无 key），验证的是 LLM 大脑在同一条接缝上的接线，
// 不是模型质量；真实端点仍由宿主侧验收。
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

await RunAsync(agents, "program", ProgramAgent());
await RunAsync(agents, "rulesEngine", RulesEngineAgent());
await RunAsync(agents, "nRules", NRulesAgent());
await RunAsync(agents, "code(precompiled)", CodeAgent());
await RunAsync(agents, "llm(scripted)", LlmAgent());

Console.WriteLine("BrainMatrix:PASS");

static async Task RunAsync(AgentFactory agents, string label, AgentDefinition definition)
{
    var agent = agents.Create(definition);
    var session = await agent.CreateSessionAsync();
    var output = string.Empty;
    await foreach (var update in agent.RunStreamingAsync("查600000", session))
    {
        output += update.Text;
    }

    if (output != "done:echo:600000")
    {
        throw new InvalidOperationException($"{label}: unexpected output '{output}'");
    }

    Console.WriteLine($"{label}:PASS");
}

// —— 五份定义：同一输入、同一工具、同一期望输出 ——

static AgentDefinition ProgramAgent() => new AgentBuilder("program-matrix")
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

static AgentDefinition RulesEngineAgent() => new AgentBuilder("rulesengine-matrix")
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

static AgentDefinition NRulesAgent() => new AgentBuilder("nrules-matrix")
    .Name("NRules matrix")
    .Engine(EngineKind.Rule)
    .BrainBackend("nRules")
    .Memory(AgentMemoryMode.Off)
    .WithTool("Echo")
    .RuleSource("""
        {
          "ruleTypes": [
            "Aether.AI.BrainMatrix.Consumer.MatrixQueryRule",
            "Aether.AI.BrainMatrix.Consumer.MatrixReplyRule"
          ]
        }
        """)
    .Build();

static AgentDefinition CodeAgent() => new AgentBuilder("code-matrix")
    .Name("Code matrix")
    .Engine(EngineKind.Code)
    .Memory(AgentMemoryMode.Off)
    .WithTool("Echo")
    .Build();

static AgentDefinition LlmAgent() => new AgentBuilder("llm-matrix")
    .Name("LLM matrix")
    .Engine(EngineKind.Llm)
    .ModelId("scripted")
    .Memory(AgentMemoryMode.Off)
    .WithTool("Echo")
    .Build();
