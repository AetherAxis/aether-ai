using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// <see cref="IChatClient"/> 包装器：从 <see cref="ChatOptions.Tools"/> 中过滤掉指定工具名，
/// 使 LLM 看不到这些工具（不会调用），但不影响 MAF 内部的 provider 状态管理。
/// <para><b>历史注记（2026-09-21）</b>：曾用于过滤 <see cref="AgentModeProvider"/> 的模式工具，但把工具名
/// 记反了（过滤 "get_mode"/"set_mode"，MAF 真实名是 "mode_get"/"mode_set"）——从未真正生效。
/// MAF 1.22 起 mode 工具禁用走原生 <c>AgentModeProviderOptions.DisableModeGetTool/SetTool</c>
/// （见 <c>AgentFactory.BuildHarnessAgent</c>），本类不再有调用方；保留作通用「按名藏工具」工具。</para>
/// </summary>
public sealed class ToolFilteringChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly HashSet<string> _hiddenToolNames;

    public ToolFilteringChatClient(IChatClient inner, params string[] hiddenToolNames)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _hiddenToolNames = new HashSet<string>(hiddenToolNames, StringComparer.OrdinalIgnoreCase);
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => _inner.GetResponseAsync(messages, FilterTools(options), cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => _inner.GetStreamingResponseAsync(messages, FilterTools(options), cancellationToken);

    private ChatOptions? FilterTools(ChatOptions? options)
    {
        if (options?.Tools is null || options.Tools.Count == 0) return options;

        var filtered = options.Tools
            .Where(t => t is not AIFunction fn || !_hiddenToolNames.Contains(fn.Name))
            .ToList();

        if (filtered.Count == options.Tools.Count) return options; // 无变化

        // 创建新的 ChatOptions（只替换 Tools，其余属性复制）
        return new ChatOptions
        {
            Instructions = options.Instructions,
            Tools = filtered,
            Temperature = options.Temperature,
            TopP = options.TopP,
            MaxOutputTokens = options.MaxOutputTokens,
            ResponseFormat = options.ResponseFormat,
            StopSequences = options.StopSequences,
            AllowMultipleToolCalls = options.AllowMultipleToolCalls,
            ToolMode = options.ToolMode,
            AdditionalProperties = options.AdditionalProperties,
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => _inner.GetService(serviceType, serviceKey);
    public void Dispose() => _inner.Dispose();
}
