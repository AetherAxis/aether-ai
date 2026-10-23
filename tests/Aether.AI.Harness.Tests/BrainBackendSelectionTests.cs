using Aether.AI.Brains.Abstractions;
using Aether.AI.Core.Agents;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Skills;
using Aether.AI.Harness.Tools;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Tests;

/// <summary>
/// 同一 <see cref="EngineKind"/> 下多实现（规则后端）的选择契约：
/// 只引用一个后端时零配置；引用多个时必须由 <see cref="AgentDefinition.BrainBackend"/> 显式指定，
/// 歧义要报错而不是按注册顺序静默取胜——后者会让「换后端」变成不可见的注册顺序副作用。
/// </summary>
public class BrainBackendSelectionTests
{
    [Fact]
    public void Single_registered_backend_needs_no_selection()
    {
        var registry = new BrainFactoryRegistry();
        registry.Register(new ProgramBackendFactory());

        Assert.True(registry.TryResolve(RuleDefinition(), out var factory));
        Assert.Equal("program", factory!.BackendId);
    }

    [Fact]
    public void Multiple_backends_without_selection_is_an_explicit_error()
    {
        var registry = new BrainFactoryRegistry();
        registry.Register(new ProgramBackendFactory());
        registry.Register(new NRulesBackendFactory());

        var error = Assert.Throws<InvalidOperationException>(
            () => registry.TryResolve(RuleDefinition(), out _));

        Assert.Contains("multiple Rule brain backends", error.Message);
        Assert.Contains("'program'", error.Message);
        Assert.Contains("'nRules'", error.Message);
        Assert.Contains("BrainBackend", error.Message);
    }

    [Fact]
    public void Declared_backend_selects_the_matching_factory()
    {
        var registry = new BrainFactoryRegistry();
        registry.Register(new ProgramBackendFactory());
        registry.Register(new NRulesBackendFactory());

        Assert.True(registry.TryResolve(RuleDefinition("nRules"), out var factory));
        Assert.IsType<NRulesBackendFactory>(factory);
        Assert.Equal("nRules", factory!.BackendId);
    }

    [Fact]
    public void Unknown_backend_fails_with_the_requested_and_registered_ids()
    {
        var registry = new BrainFactoryRegistry();
        registry.Register(new ProgramBackendFactory());
        var definition = RuleDefinition("unknown-backend");

        Assert.False(registry.TryResolve(definition, out _));

        var error = Assert.Throws<InvalidOperationException>(() => registry.Resolve(definition));
        Assert.Contains("backend 'unknown-backend'", error.Message);
        Assert.Contains("Registered backends: 'program'", error.Message);
    }

    [Fact]
    public async Task AgentFactory_runs_the_selected_rule_backend()
    {
        var registry = new BrainFactoryRegistry();
        registry.Register(new ProgramBackendFactory());
        registry.Register(new NRulesBackendFactory());
        var factory = new AgentFactory(new NullChatClientFactory(), new ToolRegistry(), new SkillRegistry(),
            brainFactories: registry);

        var agent = factory.Create(RuleDefinition("nRules"));
        var session = await agent.CreateSessionAsync();
        var text = "";
        await foreach (var update in agent.RunStreamingAsync("hi", session)) text += update.Text;

        Assert.Equal("nRules:hi", text);
    }

    [Fact]
    public void AgentBuilder_carries_the_backend_selection()
    {
        var builder = new AgentBuilder("rule")
            .Engine(EngineKind.Rule)
            .BrainBackend("program");

        Assert.Equal("program", builder.Build().BrainBackend);
    }

    private static AgentDefinition RuleDefinition(string? backend = null) => new()
    {
        Id = "rule",
        Engine = EngineKind.Rule,
        BrainBackend = backend,
        Rules = new RuleProgram { Steps = new() { new RuleStep { Action = new() { ReplyText = "ok" } } } },
    };

    private sealed class EchoBrain(string prefix) : IBrain
    {
        public ValueTask<IReadOnlyList<AIContent>> DecideAsync(BrainContext context, CancellationToken cancellationToken)
            => new(new AIContent[] { new TextContent($"{prefix}:{context.LatestUser?.Text}") });
    }

    private abstract class BackendFactory(string backendId, string prefix) : IContentBrainFactory
    {
        public EngineKind Kind => EngineKind.Rule;
        public string BackendId => backendId;
        public BrainCapabilities Capabilities => BrainCapabilities.Tools;
        public bool CanCreate(AgentDefinition definition) => definition.Engine == EngineKind.Rule;
        public IBrain Create(AgentDefinition definition) => new EchoBrain(prefix);
    }

    private sealed class ProgramBackendFactory() : BackendFactory("program", "program");

    private sealed class NRulesBackendFactory() : BackendFactory("nRules", "nRules");

    private sealed class NullChatClientFactory : IChatClientFactory
    {
        public IChatClient Create(string? modelId) => throw new NotSupportedException("rule-only test");
    }
}