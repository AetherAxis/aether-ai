using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// <see cref="IChatClient"/> 包装器：在每次发请求前对 messages 跑 <see cref="TranscriptHistoryConverter.SanitizeLive"/>，
/// 修掉孤儿 tool 消息（tool_result 无前置 tool_call / tool_call 无后续 tool_result）、空 CallId、空 tool 返回——
/// 防 DeepSeek 等 OpenAI 兼容端点报「Messages with role tool must be a response to a preceding message with tool_calls」400。
/// <para>与 <see cref="ToolFilteringChatClient"/> 同形：持 <c>_inner</c> 转发。活路径用 <see cref="TranscriptHistoryConverter.SanitizeLive"/>
/// （非 <see cref="TranscriptHistoryConverter.Sanitize"/>）——续轮请求合法地以 tool_result 收尾，不能裁尾部非 assistant。</para>
/// <para>配对完整的合法历史原样放行（幂等无副作用）。每次发送打 Debug 日志（tools 名 + 消息结构摘要）；
/// 内层抛异常时把「tools 名 + 消息摘要」拼进异常 message 抛出，让聊天页错误气泡直接显示诊断信息，
/// 便于定位 DeepSeek「Requested function not found」等端点错误的真实请求形状。</para>
/// </summary>
public sealed class ToolHistorySanitizingChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly string _label;

    public ToolHistorySanitizingChatClient(IChatClient inner, string? label = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _label = label ?? nameof(ToolHistorySanitizingChatClient);
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var sanitized = TranscriptHistoryConverter.SanitizeLive(messages);
        var tools = ToolNames(options);
        var msgSummary = Summarize(sanitized);
        Debug.WriteLine($"[{_label}] GetResponse tools={tools} msgs={msgSummary}");
        try
        {
            return await _inner.GetResponseAsync(sanitized, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (Wrap(ex, tools, msgSummary, out var wrapped))
        {
            throw wrapped;
        }
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var sanitized = TranscriptHistoryConverter.SanitizeLive(messages);
        var tools = ToolNames(options);
        var msgSummary = Summarize(sanitized);
        Debug.WriteLine($"[{_label}] GetStream tools={tools} msgs={msgSummary}");
        var stream = _inner.GetStreamingResponseAsync(sanitized, options, cancellationToken);
        return WithDiagnostics(stream, tools, msgSummary);
    }

    /// <summary>包装流式枚举：迭代中内层抛异常时，把诊断信息拼进异常 message 重新抛出。</summary>
    private async IAsyncEnumerable<ChatResponseUpdate> WithDiagnostics(
        IAsyncEnumerable<ChatResponseUpdate> stream,
        string tools, string msgSummary,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var enumerator = stream.GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                bool next;
                try { next = await enumerator.MoveNextAsync().ConfigureAwait(false); }
                catch (Exception ex) when (Wrap(ex, tools, msgSummary, out var wrapped))
                {
                    throw wrapped;
                }
                if (!next) yield break;
                yield return enumerator.Current;
            }
        }
        finally { await enumerator.DisposeAsync().ConfigureAwait(false); }
    }

    /// <summary>构造带诊断头的包装异常。返回 false（让原异常穿透）只在 Wrap 不可行时——这里恒 true。</summary>
    private static bool Wrap(Exception ex, string tools, string msgSummary, out Exception wrapped)
    {
        var diag = $"[诊断] tools=[{tools}] msgs={msgSummary} → {ex.GetType().Name}: {ex.Message}";
        Debug.WriteLine($"[ToolHistorySanitizingChatClient] 内层异常: {diag}");
        wrapped = new InvalidOperationException(diag, ex);
        return true;
    }

    private static string ToolNames(ChatOptions? options)
    {
        if (options?.Tools is null || options.Tools.Count == 0) return "(none)";
        var sb = new StringBuilder();
        foreach (var t in options.Tools)
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append(t is AIFunction fn ? fn.Name : t.GetType().Name);
        }
        return sb.ToString();
    }

    /// <summary>紧凑摘要：每条消息「角色:内容类型[CallId]」，用 | 连接——揭示顺序与配对。</summary>
    private static string Summarize(IList<ChatMessage> messages)
    {
        if (messages.Count == 0) return "[]";
        var sb = new StringBuilder();
        for (int i = 0; i < messages.Count; i++)
        {
            if (i > 0) sb.Append(" | ");
            var m = messages[i];
            sb.Append(m.Role.Value switch
            {
                "user" => "U", "assistant" => "A", "system" => "S", "tool" => "T", _ => "?",
            });
            sb.Append(':');
            foreach (var c in m.Contents)
            {
                switch (c)
                {
                    case TextContent:
                        sb.Append('t'); break;
                    case FunctionCallContent fc:
                        sb.Append("c[").Append(string.IsNullOrEmpty(fc.CallId) ? "_" : fc.CallId).Append('/').Append(fc.Name).Append(']'); break;
                    case FunctionResultContent fr:
                        sb.Append("r[").Append(string.IsNullOrEmpty(fr.CallId) ? "_" : fr.CallId).Append(']'); break;
                    default:
                        sb.Append(c.GetType().Name[0]); break;
                }
            }
        }
        return sb.ToString();
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => _inner.GetService(serviceType, serviceKey);
    public void Dispose() => _inner.Dispose();
}
