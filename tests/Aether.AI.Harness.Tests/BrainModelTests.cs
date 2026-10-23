using Aether.AI.Brains.Abstractions;
using Aether.AI.Core.Agents;

namespace Aether.AI.Harness.Tests;

public class BrainModelTests
{
    [Fact]
    public void Engine_values_preserve_legacy_serialization()
    {
        Assert.Equal(0, (int)EngineKind.Llm);
        Assert.Equal(1, (int)EngineKind.Rule);
        Assert.Equal(2, (int)EngineKind.Code);
    }

    [Theory]
    [InlineData(EngineKind.Llm, false, EngineKind.Llm)]
    [InlineData(EngineKind.Rule, false, EngineKind.Rule)]
    [InlineData(EngineKind.Rule, true, EngineKind.Code)]
    [InlineData(EngineKind.Code, false, EngineKind.Code)]
    public void Legacy_definition_maps_to_three_brain_kinds(
        EngineKind engine, bool isCodeRule, EngineKind expected)
    {
        var definition = new AgentDefinition { Engine = engine, IsCodeRule = isCodeRule };
        Assert.Equal(expected, definition.GetEffectiveEngine());
    }

    [Fact]
    public void Registry_reports_missing_opt_in_implementation()
    {
        var registry = new BrainFactoryRegistry();
        var error = Assert.Throws<InvalidOperationException>(() => registry.Resolve(
            new AgentDefinition { Id = "rule", Engine = EngineKind.Rule }));
        Assert.Contains("Rule", error.Message);
        Assert.Contains("registered", error.Message);
    }
}
