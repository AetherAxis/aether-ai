using System.Collections.Concurrent;
using Aether.AI.Harness.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace Aether.AI.Hosting;

/// <summary>
/// Default <see cref="IChatClientFactory"/>: maps a model id to an OpenAI-compatible
/// <see cref="IChatClient"/> using <see cref="AetherAIOptions.OpenAI"/> (key + endpoint).
/// A null/empty model id returns the host's default <see cref="IChatClient"/> singleton
/// (the configured default model); other model ids are built lazily and cached.
/// Hosts that resolve model ids against a richer config store (e.g. per-provider
/// LLM configs) register their own <see cref="IChatClientFactory"/> override.
/// </summary>
public sealed class DefaultChatClientFactory : IChatClientFactory
{
    private readonly Func<IChatClient> _defaultClient;
    private readonly AetherAIOptions _options;
    private readonly ConcurrentDictionary<string, IChatClient> _clients = new();

    /// <param name="defaultClient">默认客户端的<b>懒解析</b>委托（宿主 DI 传 <c>() => sp.GetRequiredService&lt;IChatClient&gt;()</c>）。
    /// 2026-10-23 修复：原构造函数直接注入 <see cref="IChatClient"/> 单例——DI 组合期（解析 HarnessRuntime 等）即触发
    /// 解析，无 OpenAI key 时纯规则/代码规则 agent 的零 LLM 宿主（console/手机）连启动都过不去。改注入委托后，
    /// <see cref="IChatClient"/> 只在首个 LLM agent 真正调用 <see cref="Create"/> 时才解析——缺 key 的错误仍会在
    /// 那一刻以原消息抛出（有 key 的宿主行为零变化）。回归钉子：tests/AddAetherAICompositionTests。</param>
    public DefaultChatClientFactory(Func<IChatClient> defaultClient, IOptions<AetherAIOptions> options)
    {
        _defaultClient = defaultClient ?? throw new ArgumentNullException(nameof(defaultClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public IChatClient Create(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId) || modelId == _options.OpenAI.ModelId)
            return _defaultClient();

        return _clients.GetOrAdd(modelId, BuildClient);
    }

    private IChatClient BuildClient(string modelId)
    {
        var apiKey = string.IsNullOrWhiteSpace(_options.OpenAI.ApiKey)
            ? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            : _options.OpenAI.ApiKey;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "No OpenAI API key configured. Set AetherAI:OpenAI:ApiKey or the OPENAI_API_KEY environment variable.");
        }

        OpenAIClient client = string.IsNullOrWhiteSpace(_options.OpenAI.Endpoint)
            ? new OpenAIClient(apiKey)
            : new OpenAIClient(
                new System.ClientModel.ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = new Uri(_options.OpenAI.Endpoint!) });
        return client.GetChatClient(modelId).AsIChatClient();
    }
}
