using Aether.AI.Core.Memory;
using Aether.AI.Hosting.Agents;
using Microsoft.Extensions.AI;

namespace Aether.AI.Memory.Tests;

/// <summary>
/// 验证 <see cref="MemoryContextProvider"/> 的召回格式化与沉淀逻辑（纯逻辑方法，不依赖 MAF 上下文对象）。
/// </summary>
public class MemoryContextProviderTests
{
    [Fact]
    public async Task Recall_returns_null_when_no_memory()
    {
        var store = new FakeMemoryStore();
        var provider = new MemoryContextProvider(store, () => "u1");
        Assert.Null(await provider.RecallInstructionsAsync("u1", default));
    }

    [Fact]
    public async Task Recall_formats_items_into_instruction_block()
    {
        var store = new FakeMemoryStore
        {
            Items = new()
            {
                new() { UserId = "u1", Type = MemoryType.UserPreference, Key = "语言", Content = "偏好简洁回答" },
                new() { UserId = "u1", Type = MemoryType.Knowledge, Key = "项目", Content = "Aether 是 .NET 平台" },
            }
        };
        var provider = new MemoryContextProvider(store, () => "u1");

        var block = await provider.RecallInstructionsAsync("u1", default);

        Assert.NotNull(block);
        Assert.Contains("用户长期记忆", block);
        Assert.Contains("[UserPreference] 语言：偏好简洁回答", block);
        Assert.Contains("[Knowledge] 项目：Aether 是 .NET 平台", block);
    }

    [Fact]
    public void BuildEpisodicMemory_returns_null_without_question()
    {
        Assert.Null(MemoryContextProvider.BuildEpisodicMemory("u1", null, "answer"));
        Assert.Null(MemoryContextProvider.BuildEpisodicMemory("u1", "   ", "answer"));
    }

    [Fact]
    public void BuildEpisodicMemory_formats_q_and_a()
    {
        var item = MemoryContextProvider.BuildEpisodicMemory("u1", "我喜欢简洁回答", "好的");
        Assert.NotNull(item);
        Assert.Equal(MemoryType.TaskHistory, item!.Type);
        Assert.Equal("u1", item.UserId);
        Assert.Equal("我喜欢简洁回答", item.Key);
        Assert.Contains("Q: 我喜欢简洁回答", item.Content);
        Assert.Contains("A: 好的", item.Content);
    }

    [Fact]
    public void ExtractTurn_pulls_last_user_question_and_assistant_answer()
    {
        var request = new[]
        {
            new ChatMessage(ChatRole.User, "first"),
            new ChatMessage(ChatRole.Assistant, "hi"),
            new ChatMessage(ChatRole.User, "second question"),
        };
        var response = new[]
        {
            new ChatMessage(ChatRole.Assistant, "the answer"),
        };

        var (question, answer) = MemoryContextProvider.ExtractTurn(request, response);

        Assert.Equal("second question", question);
        Assert.Contains("the answer", answer);
    }

    /// <summary>内存版 IMemoryStore，可预置 Items 供召回测试。</summary>
    private sealed class FakeMemoryStore : IMemoryStore
    {
        public List<MemoryItem> Items { get; set; } = new();

        public Task SaveAsync(MemoryItem item, CancellationToken cancellationToken = default)
        { Items = Items.Where(i => i.Id != item.Id).Append(item).ToList(); return Task.CompletedTask; }
        public Task<MemoryItem?> GetAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(Items.FirstOrDefault(i => i.Id == id));
        public Task<IReadOnlyList<MemoryItem>> FindByUserAsync(string? userId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<MemoryItem>>(Items.Where(i => i.UserId == userId).ToList());
        public Task<IReadOnlyList<MemoryItem>> FindByTypeAsync(string? userId, MemoryType type, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<MemoryItem>>(Items.Where(i => i.UserId == userId && i.Type == type).ToList());
        public Task<IReadOnlyList<MemoryItem>> SearchAsync(string? userId, string query, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<MemoryItem>>(Items.Where(i => i.UserId == userId && (i.Key.Contains(query) || i.Content.Contains(query))).ToList());
        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(Items.RemoveAll(i => i.Id == id) > 0);
        public Task<int> ClearAsync(string? userId, CancellationToken cancellationToken = default)
            => Task.FromResult(Items.RemoveAll(i => i.UserId == userId));
    }
}
