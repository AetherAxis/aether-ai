using Aether.AI.Brains.Abstractions;
using Aether.AI.Core.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aether.AI.Brains.Rule;

public sealed class RuleBrainFactory : IBrainFactory
{
    public EngineKind Kind => EngineKind.Rule;
    public BrainCapabilities Capabilities => BrainCapabilities.Tools;

    public bool CanCreate(AgentDefinition definition) => definition.Engine == EngineKind.Rule;

    public IBrain Create(AgentDefinition definition)
        => new RuleProgramBrain(definition.Rules ?? new RuleProgram());
}

public static class RuleBrainServiceCollectionExtensions
{
    public static IServiceCollection AddRuleBrains(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IBrainFactory, RuleBrainFactory>());
        return services;
    }
}
