using Aether.AI.Brains.Code;
using Aether.AI.Brains.Code.CSharp;
using Aether.AI.Core.Agents;
using Aether.AI.Harness.Agents;
using Aether.AI.Hosting;
using Microsoft.Extensions.DependencyInjection;

// 预编译代码大脑：作者在编译期写好 RunAsync，宿主只需把实例放进注册表并打开代码引擎。
var registry = new CodeBrainRegistry();
registry.Register("code-sample", static () => new EchoBrain());

var services = new ServiceCollection();
services.AddSingleton<ICodeBrainRegistry>(registry);
services.AddCSharpCodeBrains();
services.AddAetherAI()
    .AddTool((Func<string, string>)(text => $"echo:{text}"), "Echo");

using var provider = services.BuildServiceProvider();
var agent = provider.GetRequiredService<AgentFactory>().Create(new AgentDefinition
{
    Id = "code-sample",
    Name = "Code Sample",
    Engine = EngineKind.Code,
    Tools = new[] { "Echo" },
    Memory = AgentMemoryMode.Off,
});

var session = await agent.CreateSessionAsync();
var output = string.Empty;
await foreach (var update in agent.RunStreamingAsync("hello", session))
    output += update.Text;

Console.WriteLine(output); // done:echo:hello

/// <summary>代码大脑：用 <c>Call</c> 调工具，框架负责执行与回填。</summary>
internal sealed class EchoBrain : CodeRuleBrain
{
    protected override async Task<string> RunAsync()
        => "done:" + await Call("Echo", new { text = UserText });
}
