using Aether.AI.Core.Memory;

namespace Aether.AI.Memory;

/// <summary>
/// Persistence abstraction for long-term memory. This is the single contract
/// every Memory implementation (InMemory, SQLite, Redis, Vector) must satisfy.
/// <para>
/// Ships with <see cref="Stores.InMemoryMemoryStore"/> (platform default in
/// <c>AddAetherAI</c>) and <c>SqliteMemoryStore</c> (opt-in
/// <c>Aether.AI.Memory.Sqlite</c> package — the desktop host swaps it in).
/// Redis / vector backends remain future implementations of this interface,
/// not platform core.
/// </para>
/// </summary>
public interface IMemoryStore
{
    /// <summary>Adds or updates a memory item.</summary>
    Task SaveAsync(MemoryItem item, CancellationToken cancellationToken = default);

    /// <summary>Retrieves a single memory item by id, or <c>null</c> if not found.</summary>
    Task<MemoryItem?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns all memory items for the given user (or global when <paramref name="userId"/> is <c>null</c>).</summary>
    Task<IReadOnlyList<MemoryItem>> FindByUserAsync(string? userId, CancellationToken cancellationToken = default);

    /// <summary>Returns memory items of a specific type for the given user.</summary>
    Task<IReadOnlyList<MemoryItem>> FindByTypeAsync(string? userId, MemoryType type, CancellationToken cancellationToken = default);

    /// <summary>Performs a simple substring search over memory content.</summary>
    Task<IReadOnlyList<MemoryItem>> SearchAsync(string? userId, string query, CancellationToken cancellationToken = default);

    /// <summary>Deletes a memory item by id. Returns <c>true</c> if an item was removed.</returns>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Deletes all memory for the given user (or all memory when <paramref name="userId"/> is <c>null</c>).</summary>
    Task<int> ClearAsync(string? userId, CancellationToken cancellationToken = default);
}
