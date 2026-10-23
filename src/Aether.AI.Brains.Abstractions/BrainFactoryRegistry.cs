using Aether.AI.Core.Agents;

namespace Aether.AI.Brains.Abstractions;

/// <summary>Thread-safe registry for opt-in brain implementations.</summary>
public sealed class BrainFactoryRegistry : IBrainFactoryRegistry
{
    private readonly object _gate = new();
    private readonly List<IBrainFactory> _factories = new();

    public void Register(IBrainFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (_gate)
        {
            _factories.RemoveAll(existing => existing.GetType() == factory.GetType());
            _factories.Add(factory);
        }
    }

    public IBrainFactory Resolve(AgentDefinition definition)
    {
        if (TryResolve(definition, out var factory)) return factory!;
        throw new InvalidOperationException(
            $"No brain implementation is registered for agent '{definition.Id}' ({definition.GetEffectiveEngine()}). " +
            "Register the corresponding LLM, Rule, or Code brain package explicitly.");
    }

    public bool TryResolve(AgentDefinition definition, out IBrainFactory? factory)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var kind = definition.GetEffectiveEngine();
        lock (_gate)
        {
            factory = _factories.LastOrDefault(candidate => candidate.Kind == kind && candidate.CanCreate(definition));
            return factory is not null;
        }
    }
}
