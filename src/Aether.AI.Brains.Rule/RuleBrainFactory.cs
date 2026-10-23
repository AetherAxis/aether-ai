using Aether.AI.Brains.Abstractions;
using Aether.AI.Core.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Aether.AI.Brains.Rule;

public sealed class RuleBrainFactory : IContentBrainFactory
{
    private readonly ILoggerFactory? _loggerFactory;

    public RuleBrainFactory(ILoggerFactory? loggerFactory = null) => _loggerFactory = loggerFactory;

    public EngineKind Kind => EngineKind.Rule;
    public BrainCapabilities Capabilities => BrainCapabilities.Tools;

    public bool CanCreate(AgentDefinition definition) => definition.Engine == EngineKind.Rule;

    public IBrain Create(AgentDefinition definition)
        => new RuleProgramBrain(definition.Rules ?? new RuleProgram());

    public IChatClient CreateChatClient(AgentDefinition definition)
        => new BrainChatClient(Create(definition), _loggerFactory?.CreateLogger<BrainChatClient>());
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
