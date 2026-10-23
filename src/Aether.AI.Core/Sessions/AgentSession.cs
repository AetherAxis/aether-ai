using Aether.AI.Core.Messages;

namespace Aether.AI.Core.Sessions;

/// <summary>
/// Represents a single Agent runtime context: the running agent, the
/// conversation messages accumulated during the session, any tool calls
/// that were made, and the current execution state.
/// <para>
/// A Session is short-lived runtime state. Long-term knowledge lives in the
/// Memory layer, not here.
/// </para>
/// </summary>
public sealed class AgentSession
{
    /// <summary>Unique identifier for the session.</summary>
    public Guid SessionId { get; init; } = Guid.NewGuid();

    /// <summary>The logical identifier of the agent running in this session.</summary>
    public string AgentId { get; init; } = string.Empty;

    /// <summary>Display name of the agent running in this session.</summary>
    public string AgentName { get; init; } = string.Empty;

    /// <summary>UTC timestamp the session was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>UTC timestamp the session was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Current lifecycle state of the session.</summary>
    public SessionState State { get; set; } = SessionState.Created;

    /// <summary>The ordered conversation messages accumulated in this session.</summary>
    public List<Message> Messages { get; } = new();

    /// <summary>Names of the tools that were invoked during this session, in order.</summary>
    public List<string> ToolCalls { get; } = new();

    /// <summary>Arbitrary metadata attached to the session.</summary>
    public Dictionary<string, string?> Properties { get; } = new();

    public void Touch() => UpdatedAt = DateTimeOffset.UtcNow;
}
