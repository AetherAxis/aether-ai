using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// Resolves an <see cref="IChatClient"/> for a given agent model id. Lets a host
/// map <see cref="Aether.AI.Core.Agents.AgentDefinition.ModelId"/> (an opaque
/// string) to a concrete provider client — e.g. the desktop maps it to a stored
/// LLM config (model + endpoint + key). The platform default uses
/// <c>AetherAIOptions.OpenAI</c>.
/// </summary>
public interface IChatClientFactory
{
    /// <summary>
    /// Returns the chat client for <paramref name="modelId"/>. A null/empty
    /// modelId means "use the host default model".
    /// </summary>
    IChatClient Create(string? modelId);
}
