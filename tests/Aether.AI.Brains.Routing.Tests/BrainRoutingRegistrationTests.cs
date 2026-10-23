using Aether.AI.Brains.Abstractions;
using Aether.AI.Core.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.AI.Brains.Routing.Tests;

/// <summary>
/// 规则优先路由的组合契约：<c>AddBrainRouting</c> 只做两件事（登记选项 + 工厂），真正的规则分支
/// 在 <see cref="RuleFirstRoutingClientFactory.Create"/> 里把定义**克隆成 Rule 引擎**再交给规则大脑——
/// 克隆一旦漏字段（BrainBackend / Rules），路由就会在运行时静默走错后端或匹配不到规则。
/// </summary>
public class BrainRoutingRegistrationTests
{
    [Fact]
    public void AddBrainRouting_registers_options_and_factory()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IBrainFactoryRegistry>(new BrainFactoryRegistry());
        services.AddBrainRouting(o => o.RulePriority = 42);
        using var provider = services.BuildServiceProvider();

        Assert.Equal(42, provider.GetRequiredService<BrainRoutingOptions>().RulePriority);
        Assert.IsType<RuleFirstRoutingClientFactory>(provider.GetRequiredService<IBrainRoutingClientFactory>());
    }

    [Fact]
    public void Create_clones_the_definition_for_the_rule_branch()
    {
        var brain = new RecordingContentBrainFactory();
        var registry = new BrainFactoryRegistry();
        registry.Register(brain);
        var factory = new RuleFirstRoutingClientFactory(registry, new BrainRoutingOptions());
        var rules = new RuleProgram { Name = "rule-a" };

        var client = factory.Create(new AgentDefinition
        {
            Id = "router",
            Name = "Router",
            Engine = EngineKind.Llm,
            BrainBackend = "program",
            Rules = rules,
            Instructions = "先规则后模型",
        });

        Assert.IsType<RuleFirstRoutingChatClient>(client);
        Assert.NotNull(brain.Seen);
        Assert.Equal(EngineKind.Rule, brain.Seen!.Engine);
        Assert.Equal("program", brain.Seen.BrainBackend);
        Assert.Same(rules, brain.Seen.Rules);
        Assert.Equal("先规则后模型", brain.Seen.Instructions);
    }

    [Fact]
    public void Create_fails_loudly_when_no_rule_brain_is_registered()
    {
        var factory = new RuleFirstRoutingClientFactory(new BrainFactoryRegistry(), new BrainRoutingOptions());

        var error = Assert.Throws<InvalidOperationException>(() => factory.Create(Router()));
        Assert.Contains("AddRuleBrains", error.Message);
    }

    [Fact]
    public void Create_rejects_a_rule_brain_that_is_not_content_backed()
    {
        // Rule 引擎注册了、但它不是 IContentBrainFactory（没法直接产出内容）→ 明确报错而不是空跑。
        var registry = new BrainFactoryRegistry();
        registry.Register(new NonContentRuleFactory());
        var factory = new RuleFirstRoutingClientFactory(registry, new BrainRoutingOptions());

        var error = Assert.Throws<InvalidOperationException>(() => factory.Create(Router()));
        Assert.Contains("content brain", error.Message);
    }

    private static AgentDefinition Router() => new()
    {
        Id = "router",
        Name = "Router",
        Engine = EngineKind.Llm,
    };

    private sealed class RecordingContentBrainFactory : IContentBrainFactory
    {
        public AgentDefinition? Seen { get; private set; }

        public EngineKind Kind => EngineKind.Rule;
        public string BackendId => "program"; // 与测试定义里显式选的后端一致
        public BrainCapabilities Capabilities => BrainCapabilities.Tools;
        public bool CanCreate(AgentDefinition definition) => definition.Engine == EngineKind.Rule;

        public IBrain Create(AgentDefinition definition)
        {
            Seen = definition;
            return new NoopBrain();
        }
    }

    private sealed class NonContentRuleFactory : IBrainFactory
    {
        public EngineKind Kind => EngineKind.Rule;
        public BrainCapabilities Capabilities => BrainCapabilities.Tools;
        public bool CanCreate(AgentDefinition definition) => true;

        public IChatClient CreateChatClient(AgentDefinition definition) => throw new NotSupportedException();
    }

    private sealed class NoopBrain : IBrain
    {
        public ValueTask<IReadOnlyList<AIContent>> DecideAsync(BrainContext context, CancellationToken cancellationToken)
            => new(Array.Empty<AIContent>());
    }
}
