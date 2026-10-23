using Aether.AI.Brains.Abstractions;
using Aether.AI.Core.Agents;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Skills;
using Aether.AI.Harness.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Aether.AI.Harness.Tests;

/// <summary>
/// <c>UseChatClientAgent=true</c> 的轻量路径与 HarnessAgent 主路径的配置一致性。
/// 退出条件：轻量路径只该少「HarnessAgent 独有中间件」，不该少 skills / 长期记忆 / 后台 agent；
/// 少掉的那些必须逐条告警，不许静默忽略。
/// </summary>
public class ChatClientAgentParityTests
{
    [Fact]
    public void Lightweight_path_still_wires_the_memory_context_provider()
    {
        var sentinel = new SentinelContextProvider();
        var factory = CreateFactory(sentinel);
        factory.UseChatClientAgent = true;

        var agent = factory.Create(LlmDefinition());

        var options = Assert.IsType<ChatClientAgentOptions>(
            agent.GetService(typeof(ChatClientAgentOptions)));
        Assert.NotNull(options.AIContextProviders);
        Assert.Contains(sentinel, options.AIContextProviders!);
    }

    [Fact]
    public void Lightweight_path_keeps_the_history_provider()
    {
        var factory = CreateFactory(new SentinelContextProvider());
        factory.UseChatClientAgent = true;

        // Memory=Off 时工厂给出的是 Aether 自己的 NoMemoryChatHistoryProvider；
        // 能在这里看到它，说明轻量路径确实转发了历史 provider，而不是退回框架默认。
        var agent = factory.Create(LlmDefinition(memory: AgentMemoryMode.Off));

        var options = Assert.IsType<ChatClientAgentOptions>(
            agent.GetService(typeof(ChatClientAgentOptions)));
        Assert.IsType<NoMemoryChatHistoryProvider>(options.ChatHistoryProvider);
    }

    [Fact]
    public void Lightweight_path_warns_once_per_ignored_harness_only_setting()
    {
        var loggerFactory = new CollectingLoggerFactory();
        var factory = CreateFactory(new SentinelContextProvider(), loggerFactory);
        factory.UseChatClientAgent = true;

        factory.Create(LlmDefinition(enableTodoProvider: true, enableFileMemory: true));

        var warning = Assert.Single(loggerFactory.Messages, m => m.Contains("UseChatClientAgent=true"));
        Assert.Contains(nameof(AgentDefinition.EnableTodoProvider), warning);
        Assert.Contains(nameof(AgentDefinition.EnableFileMemory), warning);
        // 已接线的能力不该出现在告警里——否则告警变成噪音，很快没人看。
        Assert.DoesNotContain(nameof(AgentDefinition.EnableLongTermMemory), warning);
    }

    [Fact]
    public void Lightweight_path_stays_silent_when_nothing_is_ignored()
    {
        var loggerFactory = new CollectingLoggerFactory();
        var factory = CreateFactory(new SentinelContextProvider(), loggerFactory);
        factory.UseChatClientAgent = true;

        factory.Create(LlmDefinition());

        Assert.DoesNotContain(loggerFactory.Messages, m => m.Contains("UseChatClientAgent=true"));
    }

    private static AgentFactory CreateFactory(
        AIContextProvider memoryProvider, ILoggerFactory? loggerFactory = null)
        => new(
            new StubChatClientFactory(),
            new ToolRegistry(),
            new SkillRegistry(),
            loggerFactory,
            memoryContextProviderFactory: _ => memoryProvider);

    private static AgentDefinition LlmDefinition(
        bool enableTodoProvider = false,
        bool enableFileMemory = false,
        AgentMemoryMode memory = AgentMemoryMode.Full,
        int maxHistory = 20) => new()
    {
        Id = "llm-agent",
        Name = "LLM",
        Engine = EngineKind.Llm,
        Instructions = "you are a test agent",
        Memory = memory,
        MaxHistory = maxHistory,
        EnableTodoProvider = enableTodoProvider,
        EnableFileMemory = enableFileMemory,
    };

    private sealed class SentinelContextProvider : AIContextProvider;

    private sealed class StubChatClientFactory : IChatClientFactory
    {
        public IChatClient Create(string? modelId) => new StubChatClient();
    }

    private sealed class StubChatClient : IChatClient
    {
        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("streaming is not used by these tests");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    private sealed class CollectingLoggerFactory : ILoggerFactory
    {
        public List<string> Messages { get; } = new();

        public void AddProvider(ILoggerProvider provider) { }

        public ILogger CreateLogger(string categoryName) => new CollectingLogger(this);

        public void Dispose() { }

        private sealed class CollectingLogger(CollectingLoggerFactory owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => owner.Messages.Add(formatter(state, exception));
        }
    }
}
