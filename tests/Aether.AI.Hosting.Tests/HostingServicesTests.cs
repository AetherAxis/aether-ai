using Aether.AI.Brains.Abstractions;
using Aether.AI.Core.Agents;
using Aether.AI.Harness.Tools;
using Aether.AI.Hosting.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.AI.Hosting.Tests;

/// <summary>
/// Hosting 层三个「LLM 可选」服务的行为钉子。它们都跑在用户主流程旁边（补标题、压概览、查审批等级），
/// 共同契约是<b>永不把主流程拖死</b>：没有客户端、客户端抛异常、模型返回空——一律静默返回 null。
/// </summary>
public class HostingServicesTests
{
    // —— ToolApprovalLevelLookup ——

    [Fact]
    public void Approval_lookup_forwards_the_registered_level_and_defaults_to_safe()
    {
        var registry = new ToolRegistry();
        registry.Register("Delete", "删文件", "文件",
            _ => AIFunctionFactory.Create((Func<string, string>)(p => p)), ToolApprovalLevel.Dangerous);
        var lookup = new ToolApprovalLevelLookup(registry);

        Assert.Equal(ToolApprovalLevel.Dangerous, lookup.LevelOf("Delete"));
        Assert.Equal(ToolApprovalLevel.Safe, lookup.LevelOf("NotRegistered"));
    }

    // —— ConversationTitleService ——

    [Fact]
    public async Task Title_is_skipped_without_a_user_message()
    {
        var client = new RecordingChatClient("不该被调用");
        var service = NewTitleService(client);

        Assert.Null(await service.GenerateTitleAsync("   ", assistantReply: null));
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task Title_is_trimmed_and_stripped_of_quotes()
    {
        var service = NewTitleService(new RecordingChatClient("「会话标题」"));

        Assert.Equal("会话标题", await service.GenerateTitleAsync("帮我看下日志", assistantReply: null));
    }

    [Fact]
    public async Task Title_is_truncated_to_twenty_chars_and_newlines_become_spaces()
    {
        var service = NewTitleService(new RecordingChatClient(new string('标', 30)));
        Assert.Equal(20, (await service.GenerateTitleAsync("用户消息", assistantReply: null))!.Length);

        var multiline = NewTitleService(new RecordingChatClient("第一行\n第二行"));
        Assert.Equal("第一行 第二行", await multiline.GenerateTitleAsync("用户消息", assistantReply: null));
    }

    [Fact]
    public async Task Title_falls_back_to_null_when_the_model_fails_or_is_missing()
    {
        Assert.Null(await NewTitleService(new FailingChatClient()).GenerateTitleAsync("用户消息", assistantReply: null));

        var noClient = new ServiceCollection().BuildServiceProvider();
        Assert.Null(await new ConversationTitleService(noClient).GenerateTitleAsync("用户消息", assistantReply: null));
    }

    [Fact]
    public async Task Title_tolerates_a_broken_client_factory()
    {
        var services = new ServiceCollection()
            .AddSingleton<IChatClientFactory>(new ThrowingFactory())
            .BuildServiceProvider();

        Assert.Null(await new ConversationTitleService(services).GenerateTitleAsync("用户消息", assistantReply: null));
    }

    // —— ConversationSummaryService ——

    [Fact]
    public async Task Summary_is_skipped_without_text()
    {
        var client = new RecordingChatClient("不该被调用");
        var service = NewSummaryService(client);

        Assert.Null(await service.GenerateSummaryAsync("  \n ", modelId: null));
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task Summary_uses_the_agents_own_model_and_collapses_blank_lines()
    {
        var factory = new RecordingFactory(new RecordingChatClient("\"要点一\n\n\n\n\n要点二\""));
        var service = new ConversationSummaryService(
            new ServiceCollection().AddSingleton<IChatClientFactory>(factory).BuildServiceProvider());

        var summary = await service.GenerateSummaryAsync("长回复", modelId: "model-of-the-agent");

        Assert.Equal("model-of-the-agent", factory.LastModelId);
        Assert.Equal("要点一\n\n要点二", summary);
    }

    [Fact]
    public async Task Summary_truncates_long_inputs_before_sending_them()
    {
        var client = new RecordingChatClient("概览");
        var service = NewSummaryService(client);

        await service.GenerateSummaryAsync(new string('x', 9000), modelId: null);

        var prompt = client.LastUserMessage!;
        Assert.Contains("后续内容省略", prompt);
        Assert.True(prompt.Length < 9000);
    }

    [Fact]
    public async Task Summary_falls_back_to_null_when_the_model_fails_or_is_missing()
    {
        Assert.Null(await NewSummaryService(new FailingChatClient()).GenerateSummaryAsync("长回复", null));

        var noClient = new ServiceCollection().BuildServiceProvider();
        Assert.Null(await new ConversationSummaryService(noClient).GenerateSummaryAsync("长回复", null));
    }

    // —— helpers ——

    private static ConversationTitleService NewTitleService(IChatClient client)
        => new(new ServiceCollection().AddSingleton(client).BuildServiceProvider());

    private static ConversationSummaryService NewSummaryService(IChatClient client)
        => new(new ServiceCollection().AddSingleton(client).BuildServiceProvider());

    private class RecordingChatClient(string reply) : IChatClient
    {
        public int Calls { get; private set; }
        public string? LastUserMessage { get; private set; }

        public void Dispose() { }

        public virtual Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastUserMessage = messages.Last(m => m.Role == ChatRole.User).Text;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    private sealed class FailingChatClient : RecordingChatClient
    {
        public FailingChatClient() : base("") { }

        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("model down");
    }

    private sealed class ThrowingFactory : IChatClientFactory
    {
        public IChatClient Create(string? modelId) => throw new InvalidOperationException("no key");
    }

    private sealed class RecordingFactory(IChatClient client) : IChatClientFactory
    {
        public string? LastModelId { get; private set; }

        public IChatClient Create(string? modelId)
        {
            LastModelId = modelId;
            return client;
        }
    }
}
