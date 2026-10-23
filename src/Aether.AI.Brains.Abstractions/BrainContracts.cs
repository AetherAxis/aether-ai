using Aether.AI.Core.Agents;
using Microsoft.Extensions.AI;

namespace Aether.AI.Brains.Abstractions;

/// <summary>Runtime capabilities advertised by a brain implementation.</summary>
[Flags]
public enum BrainCapabilities
{
    None = 0,
    Tools = 1,
    NaturalLanguageContext = 2,
    Sampling = 4,
    Skills = 8,
    StatefulRun = 16,
}

/// <summary>Result of the side-effect-free routing match phase.</summary>
public enum BrainMatchKind
{
    NotMatched = 0,
    Matched = 1,
    NeedsInput = 2,
}

/// <summary>A routing match result; execution starts only after a handler is selected.</summary>
public sealed record BrainMatchResult(BrainMatchKind Kind, string? Reason = null)
{
    public static BrainMatchResult NotMatched(string? reason = null) => new(BrainMatchKind.NotMatched, reason);
    public static BrainMatchResult Matched(string? reason = null) => new(BrainMatchKind.Matched, reason);
    public static BrainMatchResult NeedsInput(string? reason = null) => new(BrainMatchKind.NeedsInput, reason);
}

/// <summary>Context reconstructed by the common MAF adapter for one decision step.</summary>
public class BrainContext
{
    /// <summary>Current Harness session id when available; null for standalone adapters.</summary>
    public string? SessionId { get; init; }
    public ChatMessage? LatestUser { get; init; }
    public FunctionResultContent? LatestToolResult { get; init; }
    public int ToolCallsThisTurn { get; init; }
    public IReadOnlyList<AIFunction> AvailableTools { get; init; } = Array.Empty<AIFunction>();
    public IReadOnlyList<ChatMessage> History { get; init; } = Array.Empty<ChatMessage>();
}

/// <summary>A non-LLM brain that produces a tool call or final response for one MAF step.</summary>
public interface IBrain
{
    ValueTask<IReadOnlyList<AIContent>> DecideAsync(BrainContext context, CancellationToken cancellationToken);
}

/// <summary>Optional lifecycle hook for implementations that retain pending state by session.</summary>
public interface ISessionStateOwner
{
    void EvictSession(string sessionId);
}

/// <summary>Factory for a configured brain implementation.</summary>
public interface IBrainFactory
{
    EngineKind Kind { get; }
    BrainCapabilities Capabilities { get; }
    bool CanCreate(AgentDefinition definition);
    IBrain Create(AgentDefinition definition);
}

/// <summary>Registry consumed by the harness; concrete brain projects register factories here.</summary>
public interface IBrainFactoryRegistry
{
    void Register(IBrainFactory factory);
    IBrainFactory Resolve(AgentDefinition definition);
    bool TryResolve(AgentDefinition definition, out IBrainFactory? factory);
}

/// <summary>Resolves a model client lazily for an LLM definition.</summary>
public interface IChatClientFactory
{
    IChatClient Create(string? modelId);
}

/// <summary>Creates the explicit routing composition for an agent definition.</summary>
public interface IBrainRoutingClientFactory
{
    IChatClient Create(AgentDefinition definition);
}

/// <summary>Side-effect-free matcher used by explicit routing compositions.</summary>
public interface IBrainMatcher
{
    string Id { get; }
    int Priority { get; }
    BrainMatchResult Match(AgentDefinition definition, ChatMessage input);
}
