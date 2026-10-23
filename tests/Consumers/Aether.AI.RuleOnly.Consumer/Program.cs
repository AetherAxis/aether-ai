using Aether.AI.Brains.Rule;
using Aether.AI.Core.Agents;
using Aether.AI.Harness.Agents;
using Aether.AI.Hosting;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddRuleBrains();
services.AddAetherAI()
    .AddTool((Func<string, string>)(text => $"echo:{text}"), "Echo");
using var provider = services.BuildServiceProvider();
var agent = provider.GetRequiredService<AgentFactory>()
    .Create(new AgentDefinition
    {
        Id = "rule-consumer",
        Name = "Rule Consumer",
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
await foreach (var update in agent.RunStreamingAsync("hello", session)) output += update.Text;
if (output != "done:echo:hello") throw new InvalidOperationException($"Unexpected output: {output}");
Console.WriteLine("RuleOnly:PASS");
