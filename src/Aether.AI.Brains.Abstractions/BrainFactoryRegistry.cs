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

        var requested = definition.BrainBackend;
        var selection = string.IsNullOrWhiteSpace(requested) ? string.Empty : $", backend '{requested}'";
        throw new InvalidOperationException(
            $"No brain implementation is registered for agent '{definition.Id}' " +
            $"({definition.Engine}{selection}). Registered backends: {DescribeRegistered(definition.Engine)}. " +
            "Register the corresponding brain package explicitly " +
            "(AddRuleBrains / AddLlmBrains / AddCSharpCodeBrains).");
    }

    /// <summary>
    /// Returns the single applicable factory. False means none matched; multiple matches without an explicit
    /// <see cref="AgentDefinition.BrainBackend"/> is a configuration error and throws rather than guessing.
    /// </summary>
    public bool TryResolve(AgentDefinition definition, out IBrainFactory? factory)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var kind = definition.Engine;

        List<IBrainFactory> candidates;
        lock (_gate)
        {
            candidates = _factories
                .Where(candidate => candidate.Kind == kind && candidate.CanCreate(definition))
                .ToList();
        }

        var requested = definition.BrainBackend;
        if (!string.IsNullOrWhiteSpace(requested))
        {
            candidates = candidates
                .Where(candidate => string.Equals(candidate.BackendId, requested, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        switch (candidates.Count)
        {
            case 1:
                factory = candidates[0];
                return true;
            case 0:
                factory = null;
                return false;
            default:
                throw new InvalidOperationException(
                    $"Agent '{definition.Id}' matches multiple {kind} brain backends ({Describe(candidates)}). " +
                    "Set AgentDefinition.BrainBackend to select one explicitly.");
        }
    }

    private string DescribeRegistered(EngineKind kind)
    {
        lock (_gate)
        {
            var factories = _factories.Where(factory => factory.Kind == kind).ToArray();
            return factories.Length == 0 ? "(none)" : Describe(factories);
        }
    }

    private static string Describe(IEnumerable<IBrainFactory> factories)
        => string.Join(", ", factories
            .Select(factory => $"'{factory.BackendId}' ({factory.GetType().Name})")
            .OrderBy(text => text, StringComparer.Ordinal));
}
