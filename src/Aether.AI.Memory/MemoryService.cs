using Aether.AI.Core.Conversations;
using Aether.AI.Core.Memory;

namespace Aether.AI.Memory;

/// <summary>
/// High-level facade over <see cref="IMemoryStore"/> and
/// <see cref="IConversationStore"/>. This is the API the Harness and business
/// layers talk to: it hides which concrete backend is wired up and provides
/// convenience operations for recalling and persisting agent knowledge.
/// </summary>
public sealed class MemoryService
{
    private readonly IMemoryStore _memoryStore;
    private readonly IConversationStore _conversationStore;

    public MemoryService(IMemoryStore memoryStore, IConversationStore conversationStore)
    {
        _memoryStore = memoryStore ?? throw new ArgumentNullException(nameof(memoryStore));
        _conversationStore = conversationStore ?? throw new ArgumentNullException(nameof(conversationStore));
    }

    // ---- Memory items -----------------------------------------------------

    public Task SaveMemoryAsync(MemoryItem item, CancellationToken cancellationToken = default)
        => _memoryStore.SaveAsync(item, cancellationToken);

    public Task<MemoryItem?> GetMemoryAsync(Guid id, CancellationToken cancellationToken = default)
        => _memoryStore.GetAsync(id, cancellationToken);

    public Task<IReadOnlyList<MemoryItem>> RecallAsync(string? userId, CancellationToken cancellationToken = default)
        => _memoryStore.FindByUserAsync(userId, cancellationToken);

    public Task<IReadOnlyList<MemoryItem>> RecallAsync(string? userId, MemoryType type, CancellationToken cancellationToken = default)
        => _memoryStore.FindByTypeAsync(userId, type, cancellationToken);

    public Task<IReadOnlyList<MemoryItem>> SearchMemoryAsync(string? userId, string query, CancellationToken cancellationToken = default)
        => _memoryStore.SearchAsync(userId, query, cancellationToken);

    public Task<bool> ForgetAsync(Guid id, CancellationToken cancellationToken = default)
        => _memoryStore.DeleteAsync(id, cancellationToken);

    // ---- Conversations ----------------------------------------------------

    public Task SaveConversationAsync(Conversation conversation, CancellationToken cancellationToken = default)
        => _conversationStore.SaveConversationAsync(conversation, cancellationToken);

    public Task<Conversation?> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
        => _conversationStore.GetConversationAsync(conversationId, cancellationToken);

    public Task<IReadOnlyList<Conversation>> GetConversationsAsync(string userId, CancellationToken cancellationToken = default)
        => _conversationStore.FindByUserAsync(userId, cancellationToken);
}
