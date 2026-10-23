using Aether.AI.Brains.Rule;
using Aether.AI.Core.Agents;
using Aether.AI.Harness.Agents;
using Aether.AI.Hosting;
using Microsoft.Extensions.DependencyInjection;

// 最小规则宿主：注册内置 program 后端 + 平台服务，跑一轮「输入 → 规则 → 工具 → 回填 → 终答」。
var services = new ServiceCollection();
services.AddRuleBrains();
services.AddAetherAI()
    .AddTool((Func<string, string>)(text => $"echo:{text}"), "Echo");

using var provider = services.BuildServiceProvider();
var agent = provider.GetRequiredService<AgentFactory>().Create(new AgentDefinition
{
    Id = "rule-sample",
    Name = "Rule Sample",
    Engine = EngineKind.Rule,
    Tools = new[] { "Echo" },
    Memory = AgentMemoryMode.Off,
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
                Action = new() { ReplyText = "done:{result}" },
            },
        },
    },
});

var session = await agent.CreateSessionAsync();
var output = string.Empty;
await foreach (var update in agent.RunStreamingAsync("hello", session))
    output += update.Text;

Console.WriteLine(output); // done:echo:hello
