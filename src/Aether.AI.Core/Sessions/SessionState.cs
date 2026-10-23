namespace Aether.AI.Core.Sessions;

/// <summary>
/// The lifecycle state of an <see cref="AgentSession"/>.
/// </summary>
public enum SessionState
{
    Created,
    Active,
    Idle,
    Completed,
    Faulted
}
