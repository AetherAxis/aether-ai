using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Aether.AI.Harness.Agents;

namespace Aether.AI.Hosting;

/// <summary>
/// 把 agent 一轮的完整回复（<c>fullText</c>）压成给手机端的概览。镜像
/// <see cref="IConversationTitleService"/> 的"单次 LLM 调用 → 返回字符串 → 失败静默"模式，
/// 唯一关键差异：显式传 agent 的 <c>modelId</c>（非 null）让摘要用与 agent 同款 LLM，
/// 保持术语/风格一致；标题服务传 null（用宿主默认）。供手机远程控制桌面 agent 后、
/// 发回前的结果压缩——手机端是「老板视角」，只需进度心跳 + 结果概览，不要全量过程。
/// </summary>
public interface IConversationSummaryService
{
    /// <summary>把完整回复文本压成手机端概览（≤300 字）；失败返回 null（调用方回退截断全量）。</summary>
    Task<string?> GenerateSummaryAsync(string fullText, string? modelId, CancellationToken cancellationToken = default);
}

public sealed class ConversationSummaryService : IConversationSummaryService
{
    private readonly IServiceProvider _services;

    public ConversationSummaryService(IServiceProvider services)
        => _services = services ?? throw new ArgumentNullException(nameof(services));

    public async Task<string?> GenerateSummaryAsync(string fullText, string? modelId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fullText)) return null;

        // 懒解析 IChatClient（同标题服务：优先工厂，回退容器 IChatClient）。传真实 modelId → 用与 agent 同款 LLM。
        // 工厂不可用/抛 → 静默返回 null（调用方回退截断全量，不影响发回手机）。
        IChatClient? chatClient = null;
        try
        {
            var factory = _services.GetService<IChatClientFactory>();
            if (factory != null)
                chatClient = factory.Create(modelId);
            chatClient ??= _services.GetService<IChatClient>();
        }
        catch { return null; }
        if (chatClient is null) return null;

        // 截断超长回复防上下文溢出（agent 全量回复可能很长，摘要只需前 ~8000 字足够概括）。
        const int MaxInputChars = 8000;
        var input = fullText.Length > MaxInputChars ? fullText[..MaxInputChars] + "\n…（后续内容省略）" : fullText;

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SummarySystemPrompt),
            new(ChatRole.User, input),
        };

        try
        {
            var response = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
            var summary = response.Text?.Trim();
            if (string.IsNullOrWhiteSpace(summary)) return null;
            // 去掉模型可能裹的引号/书名号；压缩多余换行。
            summary = summary.Trim('"', '\'', '「', '」', '“', '”', '《', '》');
            summary = summary.Replace("\r", "\n");
            // 多余空行压成单换行，去首尾空白行。
            while (summary.Contains("\n\n\n")) summary = summary.Replace("\n\n\n", "\n\n");
            summary = summary.Trim('\n', ' ');
            return string.IsNullOrWhiteSpace(summary) ? null : summary;
        }
        catch
        {
            return null; // 摘要失败不影响主流程（调用方回退截断全量）
        }
    }

    private const string SummarySystemPrompt =
        """"
        你是手机端概览助手。用户会给你一段桌面 AI agent 的完整回复（含推理、工具调用过程、最终结论）。
        请把它压缩成一段给手机端老板看的概览。要求：
        - 用中文，不超过 300 字
        - 直击结论与要点：做了什么、关键结果、是否成功；省略推理过程与工具细节
        - 若回复含关键代码片段/命令/文件路径/数值结果，保留最关键的（老板要据此判断），长代码只留一行引用
        - 不要寒暄、不要"根据您的请求"之类的开场白，直接给概览
        - 用紧凑的要点式（可用 markdown 短列表），不要长段落
        - 只输出概览本身，不要任何解释或前缀
        """";
}
