namespace Aether.AI.Harness.Agents;

/// <summary>
/// Compatibility alias for the public LLM factory contract. New integrations should implement
/// <see cref="Aether.AI.Brains.Abstractions.IChatClientFactory"/> directly.
/// </summary>
[Obsolete("Use Aether.AI.Brains.Abstractions.IChatClientFactory.")]
public interface IChatClientFactory : Aether.AI.Brains.Abstractions.IChatClientFactory
{
}
