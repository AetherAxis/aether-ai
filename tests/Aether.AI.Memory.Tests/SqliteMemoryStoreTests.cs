using Aether.AI.Core.Memory;
using Aether.AI.Memory.Stores;

namespace Aether.AI.Memory.Tests;

public class SqliteMemoryStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteMemoryStore _store;

    public SqliteMemoryStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mem-test-{Guid.NewGuid():N}.db");
        _store = SqliteMemoryStore.ForFile(_dbPath);
    }

    [Fact]
    public async Task Save_Get_round_trip()
    {
        var item = new MemoryItem
        {
            UserId = "u1",
            Type = MemoryType.UserPreference,
            Key = "语言",
            Content = "偏好简洁回答",
            Importance = 0.8,
        };
        await _store.SaveAsync(item);

        var loaded = await _store.GetAsync(item.Id);
        Assert.NotNull(loaded);
        Assert.Equal("u1", loaded!.UserId);
        Assert.Equal(MemoryType.UserPreference, loaded.Type);
        Assert.Equal("语言", loaded.Key);
        Assert.Equal("偏好简洁回答", loaded.Content);
        Assert.Equal(0.8, loaded.Importance);
    }

    [Fact]
    public async Task FindByUser_returns_only_that_user()
    {
        await _store.SaveAsync(new MemoryItem { UserId = "u1", Type = MemoryType.Knowledge, Key = "a", Content = "A" });
        await _store.SaveAsync(new MemoryItem { UserId = "u2", Type = MemoryType.Knowledge, Key = "b", Content = "B" });
        await _store.SaveAsync(new MemoryItem { UserId = null, Type = MemoryType.Knowledge, Key = "g", Content = "G" });

        var u1 = await _store.FindByUserAsync("u1");
        Assert.Single(u1);
        Assert.Equal("A", u1[0].Content);

        var global = await _store.FindByUserAsync(null);
        Assert.Single(global);
        Assert.Equal("G", global[0].Content);
    }

    [Fact]
    public async Task Search_matches_key_or_content()
    {
        await _store.SaveAsync(new MemoryItem { UserId = "u1", Key = "天气", Content = "北京晴" });
        await _store.SaveAsync(new MemoryItem { UserId = "u1", Key = "其它", Content = "无关内容" });

        var hits = await _store.SearchAsync("u1", "北京");
        Assert.Single(hits);
        Assert.Equal("天气", hits[0].Key);

        var byKey = await _store.SearchAsync("u1", "天气");
        Assert.Single(byKey);
    }

    [Fact]
    public async Task Save_upserts_same_id()
    {
        var id = Guid.NewGuid();
        await _store.SaveAsync(new MemoryItem { Id = id, UserId = "u1", Key = "k", Content = "old", Type = MemoryType.Knowledge });
        await _store.SaveAsync(new MemoryItem { Id = id, UserId = "u1", Key = "k", Content = "new", Type = MemoryType.Knowledge });

        var all = await _store.FindByUserAsync("u1");
        Assert.Single(all);
        Assert.Equal("new", all[0].Content);
    }

    [Fact]
    public async Task Clear_removes_user_memory_only()
    {
        await _store.SaveAsync(new MemoryItem { UserId = "u1", Key = "a", Content = "A" });
        await _store.SaveAsync(new MemoryItem { UserId = "u2", Key = "b", Content = "B" });

        var removed = await _store.ClearAsync("u1");
        Assert.Equal(1, removed);
        Assert.Empty(await _store.FindByUserAsync("u1"));
        Assert.Single(await _store.FindByUserAsync("u2"));
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal"); } catch { }
        try { if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm"); } catch { }
    }
}
