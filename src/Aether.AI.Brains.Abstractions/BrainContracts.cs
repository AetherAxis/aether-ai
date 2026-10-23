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

/// <summary>
/// Factory for one brain implementation. This is the only seam the harness uses to turn a
/// definition into a MAF transport: it does not know whether the transport is backed by a model,
/// a rule program, or an author-written program.
/// </summary>
public interface IBrainFactory
{
    EngineKind Kind { get; }

    /// <summary>What this brain can consume/produce; the harness wires providers from this, not from the engine kind.</summary>
    BrainCapabilities Capabilities { get; }

    bool CanCreate(AgentDefinition definition);

    /// <summary>Builds the MAF <see cref="IChatClient"/> that drives one agent instance.</summary>
    IChatClient CreateChatClient(AgentDefinition definition);
}

/// <summary>
/// Factory for deterministic brains authored as an <see cref="IBrain"/> (declarative rules, code programs).
/// The default transport wraps the brain in <see cref="BrainChatClient"/>; a factory only supplies the brain.
/// </summary>
public interface IContentBrainFactory : IBrainFactory
{
    IBrain Create(AgentDefinition definition);

    IChatClient IBrainFactory.CreateChatClient(AgentDefinition definition)
        => new BrainChatClient(Create(definition));
}

/// <summary>Registry consumed by the harness; concrete brain projects register factories here.</summary>
public interface IBrainFactoryRegistry
{
    void Register(IBrainFactory factory);
    IBrainFactory Resolve(AgentDefinition definition);
    bool TryResolve(AgentDefinition definition, out IBrainFactory? factory);

    /// <summary>
    /// Capabilities the harness should assume for a definition. Registered factories win; otherwise the
    /// engine-kind default applies (so a host that supplies only an <c>IChatClientFactory</c> keeps working).
    /// </summary>
    BrainCapabilities CapabilitiesFor(AgentDefinition definition)
        => TryResolve(definition, out var factory) && factory is not null
            ? factory.Capabilities
            : BrainCapabilitiesDefaults.For(definition.Engine);
}

/// <summary>Default capability sets per engine kind (used when no factory is registered).</summary>
public static class BrainCapabilitiesDefaults
{
    /// <summary>Deterministic brains: tool calls only, no natural-language understanding (code brains add <see cref="BrainCapabilities.StatefulRun"/> explicitly).</summary>
    public const BrainCapabilities Deterministic = BrainCapabilities.Tools;

    /// <summary>Model-backed brains: full MAF natural-language feature surface.</summary>
    public const BrainCapabilities Llm =
        BrainCapabilities.Tools | BrainCapabilities.NaturalLanguageContext |
        BrainCapabilities.Sampling | BrainCapabilities.Skills | BrainCapabilities.StatefulRun;

    public static BrainCapabilities For(EngineKind kind)
        => kind == EngineKind.Llm ? Llm : Deterministic;
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
