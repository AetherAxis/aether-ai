namespace Aether.AI.Core.Memory;

/// <summary>
/// A single unit of long-term memory. Memory is distinct from Session state:
/// a Session is a runtime execution context, whereas a MemoryItem is durable
/// knowledge that survives across sessions.
/// </summary>
public sealed class MemoryItem
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The user or scope this memory belongs to. <c>null</c> means global.</summary>
    public string? UserId { get; init; }

    public MemoryType Type { get; init; } = MemoryType.Knowledge;

    /// <summary>A short human-readable label for the memory.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>The full content of the memory.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>Optional relevance/importance score in the range [0,1].</summary>
    public double Importance { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Dictionary<string, string?> Metadata { get; } = new();

    public override string ToString() => $"[{Type}] {Key}: {Content}";
}
