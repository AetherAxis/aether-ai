using Aether.AI.Brains.Abstractions;
using Aether.AI.Core.Agents;
using Aether.AI.Core.Memory;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Skills;
using Aether.AI.Harness.Tools;
using Aether.AI.Hosting.Agents;
using Aether.AI.Memory.Stores;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Aether.AI.Memory.Tests;

/// <summary>
/// 长期记忆在<b>真实 agent 循环</b>里的行为（此前只测了 provider 的纯格式化方法）：
/// ① 跑之前召回的记忆要真的出现在模型输入侧（Instructions 注入），而不是只存在于 store 里；
/// ② 跑完要把本轮 Q&amp;A 沉淀成 TaskHistory，供下次会话召回；
/// ③ 记忆层故障（store 抛）不能把用户这一轮回答变成错误。
/// </summary>
public class MemoryProviderEndToEndTests
{
    [Fact]
    public async Task A_turn_injects_recalled_memory_and_stores_the_exchange()
    {
        var store = new InMemoryMemoryStore();
        await store.SaveAsync(new MemoryItem
        {
            UserId = "u1",
            Type = MemoryType.UserPreference,
            Key = "语言",
            Content = "偏好简洁回答",
        });
        var client = new RecordingChatClient();
        var agent = CreateAgent(client, store);

        await agent.RunAsync("你好", await agent.CreateSessionAsync());

        // 注入：模型确实收到了召回的记忆块（AIContext.Instructions → ChatOptions.Instructions），
        // 不是只在 store 里躺着。
        Assert.Contains("用户长期记忆", client.LastOptions?.Instructions);
        Assert.Contains("偏好简洁回答", client.LastOptions?.Instructions);

        // 沉淀：本轮问答落成 TaskHistory。
        var episodic = await store.FindByTypeAsync("u1", MemoryType.TaskHistory);
        var item = Assert.Single(episodic);
        Assert.Contains("Q: 你好", item.Content);
        Assert.Contains("A: ok", item.Content);
    }

    [Fact]
    public async Task A_broken_memory_store_does_not_fail_the_run()
    {
        var client = new RecordingChatClient();
        var agent = CreateAgent(client, new ThrowingMemoryStore());

        var response = await agent.RunAsync("你好", await agent.CreateSessionAsync());

        // 召回/沉淀都炸了，但用户照常拿到回答。
        Assert.Equal("ok", response.Text);
        Assert.NotEmpty(client.Messages);
    }

    private static AIAgent CreateAgent(IChatClient client, IMemoryStore store)
    {
        var factory = new AgentFactory(
            new StubChatClientFactory(client),
            new ToolRegistry(),
            new SkillRegistry(),
            memoryContextProviderFactory: _ => new MemoryContextProvider(store, () => "u1"));

        return factory.Create(new AgentDefinition
        {
            Id = "memory-agent",
            Name = "Memory",
            Engine = EngineKind.Llm,
            Instructions = "you are a test agent",
            EnableLongTermMemory = true,
            Memory = AgentMemoryMode.Full,
        });
    }

    private sealed class StubChatClientFactory(IChatClient client) : IChatClientFactory
    {
        public IChatClient Create(string? modelId) => client;
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public List<ChatMessage> Messages { get; } = new();
        public ChatOptions? LastOptions { get; private set; }

        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Messages.Clear();
            Messages.AddRange(messages);
            LastOptions = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("these tests use the non-streaming path");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    private sealed class ThrowingMemoryStore : IMemoryStore
    {
        public Task SaveAsync(MemoryItem item, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("store down");

        public Task<MemoryItem?> GetAsync(Guid id, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("store down");

        public Task<IReadOnlyList<MemoryItem>> FindByUserAsync(string? userId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("store down");

        public Task<IReadOnlyList<MemoryItem>> FindByTypeAsync(string? userId, MemoryType type, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("store down");

        public Task<IReadOnlyList<MemoryItem>> SearchAsync(string? userId, string query, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("store down");

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("store down");

        public Task<int> ClearAsync(string? userId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("store down");
    }
}
