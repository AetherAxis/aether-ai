namespace Aether.AI.Memory;

/// <summary>
/// Lightweight conversation summary for list views (sidebar). Does not load
/// the full message transcript — only the last message preview for display.
/// </summary>
public sealed record ConversationSummary(
    Guid ConversationId,
    string Title,
    string AgentId,
    string AgentName,
    DateTimeOffset UpdatedAt,
    string LastMessagePreview);
