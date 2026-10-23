using System.Collections.Concurrent;
using Aether.AI.Core.Sessions;

namespace Aether.AI.Harness.Sessions;

/// <summary>
/// Manages the lifecycle of <see cref="AgentSession"/> instances (the Aether
/// runtime context). This is distinct from the framework's own
/// <c>AgentSession</c> thread abstraction, which tracks model-side chat
/// history; the <see cref="HarnessRuntime"/> owns the mapping between the two.
/// </summary>
public sealed class SessionManager
{
    private readonly ConcurrentDictionary<Guid, AgentSession> _sessions = new();

    /// <summary>Creates a new runtime session for the given agent.</summary>
    public AgentSession Create(string agentId, string agentName)
    {
        var session = new AgentSession
        {
            AgentId = agentId,
            AgentName = agentName,
            State = SessionState.Active
        };
        _sessions[session.SessionId] = session;
        return session;
    }

    /// <summary>Retrieves a session by id, or <c>null</c> if not found.</summary>
    public AgentSession? Get(Guid sessionId)
        => _sessions.TryGetValue(sessionId, out var session) ? session : null;

    /// <summary>All active sessions.</summary>
    public IReadOnlyCollection<AgentSession> All => _sessions.Values.ToList();

    /// <summary>Removes a session. Returns <c>true</c> if removed.</summary>
    public bool Remove(Guid sessionId) => _sessions.TryRemove(sessionId, out _);
}
