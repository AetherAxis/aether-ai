using Aether.AI.Brains.Code;
using Aether.AI.Brains.Code.CSharp;
using Aether.AI.Core.Agents;
using Aether.AI.Harness.Agents;
using Aether.AI.Hosting;
using Microsoft.Extensions.DependencyInjection;

var registry = new CodeBrainRegistry();
registry.Register("code-consumer", static () => new EchoBrain());
var services = new ServiceCollection();
services.AddSingleton<ICodeBrainRegistry>(registry);
services.AddCSharpCodeBrains();
services.AddAetherAI()
    .AddTool((Func<string, string>)(text => $"echo:{text}"), "Echo");
using var provider = services.BuildServiceProvider();
var agent = provider.GetRequiredService<AgentFactory>()
    .Create(new AgentDefinition
    {
        Id = "code-consumer",
        Name = "Code Consumer",
        Engine = EngineKind.Code,
        Tools = new[] { "Echo" },
        Memory = AgentMemoryMode.Off,
    });
var session = await agent.CreateSessionAsync();
var output = string.Empty;
await foreach (var update in agent.RunStreamingAsync("hello", session)) output += update.Text;
if (output != "done:echo:hello") throw new InvalidOperationException($"Unexpected output: {output}");
Console.WriteLine("PrecompiledCode:PASS");

sealed class EchoBrain : CodeRuleBrain
{
    protected override async Task<string> RunAsync()
        => "done:" + await Call("Echo", new { text = UserText });
}
