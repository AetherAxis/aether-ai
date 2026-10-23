using Aether.AI.Core.Agents;
using Aether.AI.Harness;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Sessions;
using Aether.AI.Harness.Tools;
using Aether.AI.Harness.Skills;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Tests;

/// <summary>
/// Verifies the per-agent memory policy (Off / SlidingWindow / Full) by inspecting
/// the message list a fake IChatClient receives each turn.
/// </summary>
public class AgentMemoryTests
{
    [Fact]
    public async Task Off_no_history_accumulates_across_turns()
    {
        var (runtime, client) = Build(AgentMemoryMode.Off, maxHistory: 2);
        var session = runtime.StartSession("a");

        await runtime.RunStreamingAsync(session.SessionId, "turn1");
        await runtime.RunStreamingAsync(session.SessionId, "turn2");

        Assert.Equal(2, client.Received.Count);
        // 第二轮不应含第一轮历史 → 消息数与第一轮相同
        Assert.Equal(client.Received[0].Count, client.Received[1].Count);
    }

    [Fact]
    public async Task SlidingWindow_caps_history_to_maxHistory()
    {
        var (runtime, client) = Build(AgentMemoryMode.SlidingWindow, maxHistory: 2);
        var session = runtime.StartSession("a");

        await runtime.RunStreamingAsync(session.SessionId, "turn1");
        await runtime.RunStreamingAsync(session.SessionId, "turn2");
        await runtime.RunStreamingAsync(session.SessionId, "turn3");

        Assert.Equal(3, client.Received.Count);
        // 窗口满后历史不再增长：第三轮消息数 == 第二轮（2 条历史 + 1 当前）
        Assert.Equal(client.Received[1].Count, client.Received[2].Count);
    }

    [Fact]
    public async Task Full_grows_history_unbounded()
    {
        var (runtime, client) = Build(AgentMemoryMode.Full, maxHistory: 2);
        var session = runtime.StartSession("a");

        await runtime.RunStreamingAsync(session.SessionId, "turn1");
        await runtime.RunStreamingAsync(session.SessionId, "turn2");
        await runtime.RunStreamingAsync(session.SessionId, "turn3");

        Assert.Equal(3, client.Received.Count);
        // 每轮历史递增
        Assert.True(client.Received[0].Count < client.Received[1].Count);
        Assert.True(client.Received[1].Count < client.Received[2].Count);
    }

    private static (HarnessRuntime runtime, RecordingChatClient client) Build(AgentMemoryMode memory, int maxHistory)
    {
        var client = new RecordingChatClient();
        var factory = new AgentFactory(new ConstChatClientFactory(client), new ToolRegistry(), new SkillRegistry());
        var runtime = new HarnessRuntime(factory, new SessionManager());
        runtime.RegisterAgent(new AgentDefinition
        {
            Id = "a",
            Name = "A",
            Instructions = "be brief",
            Memory = memory,
            MaxHistory = maxHistory,
        });
        return (runtime, client);
    }

    private sealed class ConstChatClientFactory : IChatClientFactory
    {
        private readonly IChatClient _client;
        public ConstChatClientFactory(IChatClient client) => _client = client;
        public IChatClient Create(string? modelId) => _client;
    }

    /// <summary>记录每次收到的消息列表，并返回一条固定流式响应。</summary>
    private sealed class RecordingChatClient : IChatClient
    {
        public List<IList<ChatMessage>> Received { get; } = new();

        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Received.Add(messages.ToList());
            return Stream();
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> Stream([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate { Contents = new AIContent[] { new TextContent("ok") } };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }
}
