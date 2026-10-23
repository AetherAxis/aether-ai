using Aether.AI.Brains.Abstractions;
using Aether.AI.Brains.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.AI.Brains.Llm.Tests;

/// <summary>
/// LLM 客户端的解析契约：默认模型走宿主单例、其他模型按需构建并缓存、无 key 时才报错
/// （且报错要发生在「首次真正用模型」那一刻，而不是 DI 组合期）。
/// </summary>
public class ChatClientFactoryTests
{
    [Fact]
    public void Empty_and_default_model_ids_route_to_the_host_client()
    {
        var calls = 0;
        var factory = new DefaultChatClientFactory(() => { calls++; return new StubClient(); }, new LlmBrainOptions());

        factory.Create(null);
        factory.Create("");
        factory.Create(new LlmBrainOptions().ModelId);

        Assert.Equal(3, calls);
    }

    [Fact]
    public void Other_model_ids_are_built_once_and_cached()
    {
        var factory = new DefaultChatClientFactory(
            () => new StubClient(),
            new LlmBrainOptions { ApiKey = "sk-test-not-a-real-key" });

        var first = factory.Create("model-a");
        var second = factory.Create("model-a");
        var other = factory.Create("model-b");

        Assert.Same(first, second);
        Assert.NotSame(first, other);
    }

    [Fact]
    public void Missing_api_key_fails_only_when_a_model_actually_needs_building()
    {
        var saved = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        try
        {
            var factory = new DefaultChatClientFactory(() => new StubClient(), new LlmBrainOptions());

            // 默认模型不碰 key（由宿主提供客户端）。
            Assert.NotNull(factory.Create(null));

            var error = Assert.Throws<InvalidOperationException>(() => factory.Create("some-model"));
            Assert.Contains("OPENAI_API_KEY", error.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", saved);
        }
    }

    [Fact]
    public void AddLlmBrains_wires_options_factory_and_brain_without_a_key()
    {
        var saved = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        try
        {
            var services = new ServiceCollection();
            services.AddLlmBrains(o => o.ModelId = "custom-model");
            using var provider = services.BuildServiceProvider();

            // 组合期不解析 IChatClient —— 无 key 的纯规则宿主也要能启动。
            Assert.Equal("custom-model", provider.GetRequiredService<LlmBrainOptions>().ModelId);

            var factory = Assert.IsType<DefaultChatClientFactory>(provider.GetRequiredService<IChatClientFactory>());
            Assert.NotNull(factory);

            var brain = Assert.Single(provider.GetServices<IBrainFactory>());
            Assert.Equal("Llm", brain.BackendId);
            Assert.True(brain.Capabilities.HasFlag(BrainCapabilities.Sampling));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", saved);
        }
    }

    private sealed class StubClient : IChatClient
    {
        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }
}
