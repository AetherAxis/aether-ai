using Aether.AI.Core.Agents;
using Aether.AI.Harness;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Sessions;
using Aether.AI.Harness.Tools;
using Aether.AI.Harness.Skills;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Tests;

public class HarnessRuntimeTests
{
    [Fact]
    public void StartSession_throws_for_unregistered_agent()
    {
        var runtime = new HarnessRuntime(NewFactory(), new SessionManager());

        var ex = Assert.Throws<InvalidOperationException>(() => runtime.StartSession("nope"));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public void RegisterAgent_then_StartSession_creates_session()
    {
        var runtime = new HarnessRuntime(NewFactory(), new SessionManager());
        runtime.RegisterAgent(new AgentDefinition
        {
            Id = "demo",
            Name = "Demo",
            Instructions = "be brief",
            ModelId = "gpt-4o-mini"
        });

        var session = runtime.StartSession("demo");

        Assert.Equal("demo", session.AgentId);
        Assert.Equal("Demo", session.AgentName);
        Assert.True(runtime.HasAgent("demo"));
    }

    private static AgentFactory NewFactory()
    {
        // A null chat client is fine here — these tests never invoke the model,
        // they only exercise registration and session creation.
        return new AgentFactory(new NullChatClientFactory(), new ToolRegistry(), new SkillRegistry());
    }

    /// <summary>A minimal IChatClient that never produces responses; used only
    /// to satisfy AgentFactory construction in unit tests that don't run the model.</summary>
    private sealed class NullChatClient : IChatClient
    {
        public void Dispose() { }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    /// <summary>An IChatClientFactory that always hands back a NullChatClient.</summary>
    private sealed class NullChatClientFactory : IChatClientFactory
    {
        public IChatClient Create(string? modelId) => new NullChatClient();
    }
}
