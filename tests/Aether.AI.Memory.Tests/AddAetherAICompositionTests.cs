using Aether.AI.Brains.Rule;
using Aether.AI.Core.Agents;
using Aether.AI.Harness;
using Aether.AI.Hosting;
using Aether.AI.Harness.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.AI.Memory.Tests;

/// <summary>
/// <c>AddAetherAI</c> DI 组合钉子（2026-10-23 修复的回归测试）：零 LLM 宿主（纯规则 agent——
/// console / 手机形态）在<b>无 OpenAI key 且不覆盖 IChatClientFactory</b> 的前提下必须能完成
/// DI 组合并跑通一轮。修复前 <c>DefaultChatClientFactory</c> 构造期即解析 <c>IChatClient</c>，
/// 此处解析 HarnessRuntime 时即抛「No OpenAI API key configured」。
/// <para>放本测试项目的原因：Memory.Tests 是唯一引用 Hosting 的测试项目（MemoryContextProvider /
/// MemoryTool 两文件同此先例）。</para>
/// </summary>
public class AddAetherAICompositionTests
{
    [Fact]
    public void Legacy_chat_client_factory_registration_is_bridged()
    {
        var services = new ServiceCollection();
        var legacyFactory = new LegacyChatClientFactory();
#pragma warning disable CS0618 // Exercise the former public DI service key.
        services.AddSingleton<Aether.AI.Harness.Agents.IChatClientFactory>(legacyFactory);
#pragma warning restore CS0618
        services.AddAetherAI();

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<Aether.AI.Harness.Agents.AgentFactory>();
        var agent = factory.Create(new AgentDefinition
        {
            Id = "legacy-llm-factory",
            Name = "legacy",
            Engine = EngineKind.Llm,
        });

        Assert.NotNull(agent);
        Assert.Equal(1, legacyFactory.CreateCalls);
    }

    [Fact]
    public async Task ZeroLlmHost_boots_and_runs_rule_agent_without_key_or_factory_override()
    {
        // 前置：确保无 key（保存/还原——本程序集无其它用例读该 env）
        var saved = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        try
        {
            var services = new ServiceCollection();
            services.AddRuleBrains();
            var builder = services.AddAetherAI();

            builder.ConfigureToolRegistry(tools => tools.Register(
                "Echo", "回声", "测试",
                _ => AIFunctionFactory.Create((Func<string, string>)(text => $"echo:{text}"), name: "Echo")));

            builder.AddAgent("zero-llm", ab => ab
                .Name("zero-llm").Instructions("t")
                .Engine(EngineKind.Rule)
                .Rules(new RuleProgram
                {
                    Steps = new()
                    {
                        new RuleStep { Match = new() { OnUserRegex = "^(.+)", MaxToolCalls = 0 }, Action = new() { Tool = "Echo", Args = new() { ["text"] = "{user.1}" } } },
                        new RuleStep { Match = new() { MinToolCalls = 1 }, Action = new() { ReplyText = "终答:{result}" } },
                    },
                })
                .WithTool("Echo"));

            // 修复前：下一行抛 InvalidOperationException（组合期解析 IChatClient）
            using var sp = services.BuildServiceProvider();
            var runtime = sp.GetRequiredService<HarnessRuntime>();

            var session = runtime.StartSession("zero-llm");
            var sb = new System.Text.StringBuilder();
            await runtime.RunStreamingAsync(session.SessionId, "hello",
                new StreamHandlers { OnToken = (t, _) => { sb.Append(t); return Task.CompletedTask; } });

            Assert.Contains("echo:hello", sb.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", saved);
        }
    }

#pragma warning disable CS0618 // Compatibility test implementation.
    private sealed class LegacyChatClientFactory : Aether.AI.Harness.Agents.IChatClientFactory
#pragma warning restore CS0618
    {
        public int CreateCalls { get; private set; }

        public IChatClient Create(string? modelId)
        {
            CreateCalls++;
            return new StubChatClient();
        }
    }

    private sealed class StubChatClient : IChatClient
    {
        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = new List<AIContent> { new TextContent("ok") },
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }
}
