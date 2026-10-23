using System.Runtime.CompilerServices;
using System.Text.Json;
using Aether.AI.Brains.Abstractions;
using Aether.AI.Core.Agents;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Sessions;
using Aether.AI.Harness.Skills;
using Aether.AI.Harness.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Tests;

/// <summary>
/// 会话持久化端口（<see cref="IAgentSessionStore"/>）的契约：
/// ① 配了存储就每轮落盘，未配则一行都不写（默认行为不变）；
/// ② 重启（新 runtime + 同一存储 + 同一 sessionId）能把上一进程的框架会话接回来；
/// ③ 删除/重建会话要同时删掉落盘那份，否则下次启动会把已删会话「复活」；
/// ④ MAF 的 <see cref="Microsoft.Agents.AI.AgentSessionStore"/> 能与同一份存储互通。
/// </summary>
public class AgentSessionStoreTests
{
    [Fact]
    public async Task Session_is_persisted_after_each_turn()
    {
        var store = new InMemoryAgentSessionStore();
        var runtime = NewRuntime(store);
        var session = runtime.StartSession("llm-agent");

        var reply = await RunAsync(runtime, session.SessionId, "hello");

        Assert.Equal("msgs:1", reply);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task Without_a_store_nothing_is_written()
    {
        var runtime = NewRuntime(sessionStore: null);
        var session = runtime.StartSession("llm-agent");

        var reply = await RunAsync(runtime, session.SessionId, "hello");

        Assert.Equal("msgs:1", reply); // 仍能跑——只是不落盘
    }

    [Fact]
    public async Task Restart_reuses_the_persisted_framework_session()
    {
        var store = new InMemoryAgentSessionStore();
        var first = NewRuntime(store);
        var session = first.StartSession("llm-agent");
        Assert.Equal("msgs:1", await RunAsync(first, session.SessionId, "hello"));

        // 模拟进程重启：全新 runtime / 工厂 / SessionManager，只有存储是同一份。
        var restarted = NewRuntime(store);
        var resumed = restarted.StartSession("llm-agent", session.SessionId);
        var reply = await RunAsync(restarted, resumed.SessionId, "again");

        // 历史（user:hello / assistant:msgs:1）跟着会话恢复 → 第二次看到 3 条消息；
        // 若恢复失效则是 1 条。
        Assert.Equal("msgs:3", reply);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task Evict_removes_the_persisted_session()
    {
        var store = new InMemoryAgentSessionStore();
        var runtime = NewRuntime(store);
        var session = runtime.StartSession("llm-agent");
        await RunAsync(runtime, session.SessionId, "hello");
        Assert.Equal(1, store.Count);

        runtime.EvictSession(session.SessionId);

        await EventuallyAsync(() => store.Count == 0);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task Rebuild_drops_the_persisted_session_so_it_is_not_resurrected()
    {
        var store = new InMemoryAgentSessionStore();
        var runtime = NewRuntime(store);
        var session = runtime.StartSession("llm-agent");
        await RunAsync(runtime, session.SessionId, "hello");

        runtime.RebuildSessionFramework(session.SessionId);
        await EventuallyAsync(() => store.Count == 0);

        // 重建后下一轮从干净会话开始（不是从存储里把旧状态捞回来）。
        Assert.Equal("msgs:1", await RunAsync(runtime, session.SessionId, "again"));
    }

    [Fact]
    public async Task Maf_store_adapter_round_trips_through_the_same_storage()
    {
        var inner = new InMemoryAgentSessionStore();
        var bridge = new MafAgentSessionStore(inner);
        var agent = NewFactory().Create(LlmDefinition());
#pragma warning disable MAAI001 // AgentSessionStoreKey 在 Abstractions 1.24 仍是实验性 API；这里只做互通钉子
        var key = new AgentSessionStoreKey("conversation-1", new Dictionary<string, string>());

        var created = await bridge.GetOrCreateSessionAsync(agent, key);
        Assert.NotNull(created);
        Assert.Equal(1, inner.Count);
        // 键格式与运行时一致（agent 身份 + 会话 id）：MAF 侧写进来的会话，Aether 侧读得到。
        Assert.NotNull(await inner.GetAsync("llm-agent|conversation-1"));

        // 同一个 key 再取 → 从存储反序列化（不是新建），并且能继续跑。
        var restored = await bridge.GetSessionAsync(agent, key);
        Assert.NotNull(restored);
        Assert.NotSame(created, restored);

        var reply = await agent.RunAsync("hello", restored);
        Assert.Equal("msgs:1", reply.Text);
    }

    [Fact]
    public async Task Maf_store_adapter_returns_null_for_missing_keys_instead_of_inventing_sessions()
    {
        var bridge = new MafAgentSessionStore(new InMemoryAgentSessionStore());
        var agent = NewFactory().Create(LlmDefinition());
        var key = new AgentSessionStoreKey("nope", new Dictionary<string, string>());
#pragma warning restore MAAI001

        // MAF 契约：GetSessionAsync 查不到返回 null（本方法永不新建会话）。
        Assert.Null(await bridge.GetSessionAsync(agent, key));
    }

    [Fact]
    public async Task Maf_store_adapter_deletes_through_to_the_inner_store()
    {
        var inner = new InMemoryAgentSessionStore();
        var bridge = new MafAgentSessionStore(inner);
        var agent = NewFactory().Create(LlmDefinition());
#pragma warning disable MAAI001 // AgentSessionStoreKey 在 Abstractions 1.24 仍是实验性 API；这里只做互通钉子
        var key = new AgentSessionStoreKey("to-delete", new Dictionary<string, string>());

        await bridge.GetOrCreateSessionAsync(agent, key);
        Assert.NotNull(await inner.GetAsync("llm-agent|to-delete"));

        // MAF 1.26 起 DeleteSessionAsync 是基类抽象成员——不转发会让已删会话下次启动「复活」。
        await bridge.DeleteSessionAsync(agent, key);
#pragma warning restore MAAI001

        Assert.Null(await inner.GetAsync("llm-agent|to-delete"));
        Assert.Null(await bridge.GetSessionAsync(agent, key));
    }

    // —— helpers ——

    private static HarnessRuntime NewRuntime(IAgentSessionStore? sessionStore)
    {
        var runtime = new HarnessRuntime(NewFactory(), new SessionManager(), agentSessionStore: sessionStore);
        runtime.RegisterAgent(LlmDefinition());
        return runtime;
    }

    private static Task<string> RunAsync(HarnessRuntime runtime, Guid sessionId, string input)
        => runtime.RunStreamingAsync(sessionId, input);

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(10);
    }

    private static AgentFactory NewFactory()
        => new(new ScriptedChatClientFactory(), new ToolRegistry(), new SkillRegistry());

    private static AgentDefinition LlmDefinition() => new()
    {
        Id = "llm-agent",
        Name = "LLM",
        Engine = EngineKind.Llm,
        Instructions = "test agent",
        Memory = AgentMemoryMode.Full,
    };

    /// <summary>把收到的消息条数回声成 <c>msgs:N</c>——历史是否随会话恢复一眼可见。</summary>
    private sealed class ScriptedChatClientFactory : IChatClientFactory
    {
        public IChatClient Create(string? modelId) => new EchoCountChatClient();
    }

    private sealed class EchoCountChatClient : IChatClient
    {
        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Reply(messages))));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, Reply(messages));
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        private static string Reply(IEnumerable<ChatMessage> messages) => $"msgs:{messages.Count()}";
    }
}
