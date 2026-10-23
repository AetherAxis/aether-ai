using System.Collections.Concurrent;
using Aether.AI.Core.Memory;

namespace Aether.AI.Memory.Stores;

/// <summary>
/// A thread-safe, in-process implementation of <see cref="IMemoryStore"/>. Used
/// for demos, tests, and short-lived scenarios. Not persisted across process
/// restarts.
/// </summary>
public sealed class InMemoryMemoryStore : IMemoryStore
{
    private readonly ConcurrentDictionary<Guid, MemoryItem> _items = new();

    public Task SaveAsync(MemoryItem item, CancellationToken cancellationToken = default)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        // Replace any existing item with the same id, bumping UpdatedAt.
        item.UpdatedAt = DateTimeOffset.UtcNow;
        _items[item.Id] = item;
        return Task.CompletedTask;
    }

    public Task<MemoryItem?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _items.TryGetValue(id, out var item);
        return Task.FromResult(item);
    }

    public Task<IReadOnlyList<MemoryItem>> FindByUserAsync(string? userId, CancellationToken cancellationToken = default)
    {
        var results = _items.Values
            .Where(i => i.UserId == userId)
            .OrderByDescending(i => i.UpdatedAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<MemoryItem>>(results);
    }

    public Task<IReadOnlyList<MemoryItem>> FindByTypeAsync(string? userId, MemoryType type, CancellationToken cancellationToken = default)
    {
        var results = _items.Values
            .Where(i => i.UserId == userId && i.Type == type)
            .OrderByDescending(i => i.UpdatedAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<MemoryItem>>(results);
    }

    public Task<IReadOnlyList<MemoryItem>> SearchAsync(string? userId, string query, CancellationToken cancellationToken = default)
    {
        var comparison = StringComparison.OrdinalIgnoreCase;
        var results = _items.Values
            .Where(i => i.UserId == userId)
            .Where(i => i.Content.Contains(query, comparison) || i.Key.Contains(query, comparison))
            .OrderByDescending(i => i.UpdatedAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<MemoryItem>>(results);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(_items.TryRemove(id, out _));

    public Task<int> ClearAsync(string? userId, CancellationToken cancellationToken = default)
    {
        if (userId is null)
        {
            var count = _items.Count;
            _items.Clear();
            return Task.FromResult(count);
        }

        var toRemove = _items.Values.Where(i => i.UserId == userId).Select(i => i.Id).ToList();
        foreach (var id in toRemove) _items.TryRemove(id, out _);
        return Task.FromResult(toRemove.Count);
    }
}
