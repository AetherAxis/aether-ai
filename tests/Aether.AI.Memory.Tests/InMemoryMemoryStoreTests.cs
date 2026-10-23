using Aether.AI.Core.Memory;
using Aether.AI.Memory.Stores;

namespace Aether.AI.Memory.Tests;

public class InMemoryMemoryStoreTests
{
    [Fact]
    public async Task Save_and_Get_returns_item()
    {
        var store = new InMemoryMemoryStore();
        var item = new MemoryItem { UserId = "u1", Type = MemoryType.Knowledge, Key = "k", Content = "c" };

        await store.SaveAsync(item);

        var loaded = await store.GetAsync(item.Id);
        Assert.NotNull(loaded);
        Assert.Equal("c", loaded!.Content);
    }

    [Fact]
    public async Task FindByUser_isolates_users()
    {
        var store = new InMemoryMemoryStore();
        await store.SaveAsync(new MemoryItem { UserId = "u1", Key = "a", Content = "a" });
        await store.SaveAsync(new MemoryItem { UserId = "u2", Key = "b", Content = "b" });
        await store.SaveAsync(new MemoryItem { UserId = null, Key = "g", Content = "g" });

        var u1 = await store.FindByUserAsync("u1");
        Assert.Single(u1);
        Assert.Equal("a", u1[0].Content);
    }

    [Fact]
    public async Task Search_matches_content_and_key()
    {
        var store = new InMemoryMemoryStore();
        await store.SaveAsync(new MemoryItem { UserId = "u1", Key = "weather", Content = "rainy" });
        await store.SaveAsync(new MemoryItem { UserId = "u1", Key = "news", Content = "weather report" });

        var hits = await store.SearchAsync("u1", "weather");
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public async Task Delete_removes_item()
    {
        var store = new InMemoryMemoryStore();
        var item = new MemoryItem { UserId = "u1", Content = "x" };
        await store.SaveAsync(item);

        var removed = await store.DeleteAsync(item.Id);
        Assert.True(removed);
        Assert.Null(await store.GetAsync(item.Id));
    }

    [Fact]
    public async Task Clear_removes_only_given_user()
    {
        var store = new InMemoryMemoryStore();
        await store.SaveAsync(new MemoryItem { UserId = "u1", Content = "a" });
        await store.SaveAsync(new MemoryItem { UserId = "u2", Content = "b" });

        var removed = await store.ClearAsync("u1");
        Assert.Equal(1, removed);
        Assert.Empty(await store.FindByUserAsync("u1"));
        Assert.Single(await store.FindByUserAsync("u2"));
    }
}
