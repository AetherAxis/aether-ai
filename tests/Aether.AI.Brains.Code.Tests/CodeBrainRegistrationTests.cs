using Aether.AI.Brains.Abstractions;
using Aether.AI.Brains.Code.CSharp;
using Aether.AI.Brains.Code.FSharp;
using Aether.AI.Core.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.AI.Brains.Code.Tests;

/// <summary>
/// 代码大脑的组合入口：C#/F# 两个包都只是「打开代码引擎」，重复调用必须幂等；
/// 而「大脑还没编译好/编译失败」不能抛到调用方——要退化成一条可读的回复。
/// </summary>
public class CodeBrainRegistrationTests
{
    [Fact]
    public void Both_language_packages_enable_one_code_engine()
    {
        var services = new ServiceCollection();
        services.AddCSharpCodeBrains();
        services.AddFSharpCodeBrains();
        using var provider = services.BuildServiceProvider();

        var factory = Assert.Single(provider.GetServices<IBrainFactory>());
        Assert.IsType<CodeBrainFactory>(factory);
        Assert.Equal(EngineKind.Code, factory.Kind);
        Assert.NotNull(provider.GetService<ICodeBrainRegistry>());
    }

    [Fact]
    public void Factory_returns_a_fresh_brain_per_create()
    {
        var registry = new CodeBrainRegistry();
        registry.Register("program", () => new ReplyBrain("ok"));
        var factory = new CodeBrainFactory(registry);
        var definition = new AgentDefinition { Id = "program", Name = "Program", Engine = EngineKind.Code };

        Assert.True(factory.CanCreate(definition));
        Assert.NotSame(factory.Create(definition), factory.Create(definition));
    }

    [Fact]
    public async Task Missing_code_brain_degrades_to_a_reply_instead_of_throwing()
    {
        var registry = new CodeBrainRegistry();
        registry.RecordError("broken", "CS1002: 需要 ;");
        var factory = new CodeBrainFactory(registry);

        var notRegistered = await ReplyAsync(factory, "unknown");
        Assert.Contains("代码大脑未注册", notRegistered);

        var compileFailed = await ReplyAsync(factory, "broken");
        Assert.Contains("代码大脑编译失败", compileFailed);
        Assert.Contains("CS1002", compileFailed);
    }

    private static async Task<string> ReplyAsync(CodeBrainFactory factory, string agentId)
    {
        var client = factory.CreateChatClient(new AgentDefinition
        {
            Id = agentId,
            Name = agentId,
            Engine = EngineKind.Code,
        });
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        return response.Text ?? "";
    }

    private sealed class ReplyBrain(string text) : IBrain
    {
        public ValueTask<IReadOnlyList<AIContent>> DecideAsync(BrainContext context, CancellationToken cancellationToken)
            => new(new AIContent[] { new TextContent(text) });
    }
}
