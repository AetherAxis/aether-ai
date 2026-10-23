using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Aether.AI.Harness.Agents;

namespace Aether.AI.Hosting;

/// <summary>
/// 根据首轮对话内容用 LLM 生成简短会话标题。镜像 AlphaAgent 的 IAgentTitleService：
/// 首轮后异步补标题、不阻塞对话、失败静默。优先用 <see cref="IChatClientFactory.Create"/>（null=宿主默认
/// 模型）解析客户端——桌面端映射到所选 LLM 配置，手机端映射到用户所选 LLM（手机端无全局默认
/// <c>IChatClient</c>，必须经工厂）。工厂不可用时回退到容器中注册的 <c>IChatClient</c>。
/// </summary>
public interface IConversationTitleService
{
    /// <summary>根据首条用户消息（+首条助手回复）生成简短标题；失败返回 null。</summary>
    Task<string?> GenerateTitleAsync(string userMessage, string? assistantReply, CancellationToken cancellationToken = default);
}

public sealed class ConversationTitleService : IConversationTitleService
{
    private readonly IServiceProvider _services;

    public ConversationTitleService(IServiceProvider services)
        => _services = services ?? throw new ArgumentNullException(nameof(services));

    public async Task<string?> GenerateTitleAsync(string userMessage, string? assistantReply, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage)) return null;

        // 懒解析 IChatClient：优先经 IChatClientFactory（手机端只有工厂，无全局 IChatClient），
        // 回退到容器中的 IChatClient（平台默认）。解析抛/为空时静默退化为不生成标题（不影响主流程）
        IChatClient? chatClient = null;
        try
        {
            var factory = _services.GetService<IChatClientFactory>();
            if (factory != null)
                chatClient = factory.Create(null);
            chatClient ??= _services.GetService<IChatClient>();
        }
        catch { return null; }
        if (chatClient is null) return null;

        var prompt = BuildPrompt(userMessage, assistantReply);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, TitleSystemPrompt),
            new(ChatRole.User, prompt),
        };

        try
        {
            var response = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
            var title = response.Text?.Trim().Trim('"', '\'', '「', '」', '“', '”', '《', '》', '.', '。', ' ');
            if (string.IsNullOrWhiteSpace(title)) return null;
            if (title.Length > 20) title = title[..20];
            title = title.Replace("\n", " ").Replace("\r", " ").Trim();
            return string.IsNullOrWhiteSpace(title) ? null : title;
        }
        catch
        {
            return null; // 标题生成失败不影响主流程
        }
    }

    private const string TitleSystemPrompt =
        """"
        你是一个会话标题生成助手。请根据用户的首条消息（和助手的回复）生成一个简短的会话标题。
        要求：
        - 用中文，4-12 个字
        - 概括用户的核心意图或话题，不要泛泛而谈
        - 不要加引号、书名号、句号等标点
        - 只输出标题本身，不要任何解释或前缀
        """";

    private static string BuildPrompt(string userMessage, string? assistantReply)
    {
        var text = $"用户首条消息：\n{userMessage}";
        if (!string.IsNullOrWhiteSpace(assistantReply))
        {
            var replySnippet = assistantReply.Length > 300 ? assistantReply[..300] : assistantReply;
            text += $"\n\n助手回复（节选）：\n{replySnippet}";
        }
        text += "\n\n请生成会话标题：";
        return text;
    }
}
