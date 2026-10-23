using Aether.AI.Brains.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using OpenAI.Chat;

namespace Aether.AI.Brains.Llm;

public sealed class LlmBrainOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string ModelId { get; set; } = "gpt-4o-mini";
    public string? Endpoint { get; set; }
}

public static class LlmBrainServiceCollectionExtensions
{
    public static IServiceCollection AddLlmBrains(
        this IServiceCollection services,
        Action<LlmBrainOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new LlmBrainOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton<IChatClient>(_ => BuildClient(options, options.ModelId));
        services.AddSingleton<IChatClientFactory>(sp =>
            new DefaultChatClientFactory(() => sp.GetRequiredService<IChatClient>(), options));
        // LLM 与 Rule/Code 同经 IBrainFactory 解析——Harness 不再对 EngineKind 做大脑分支。
        services.AddLlmBrainFactory();
        return services;
    }

    private static IChatClient BuildClient(LlmBrainOptions options, string modelId)
    {
        var apiKey = string.IsNullOrWhiteSpace(options.ApiKey)
            ? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            : options.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "No OpenAI API key configured. Configure AddLlmBrains or set OPENAI_API_KEY.");

        OpenAIClient client = string.IsNullOrWhiteSpace(options.Endpoint)
            ? new OpenAIClient(apiKey)
            : new OpenAIClient(
                new System.ClientModel.ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = new Uri(options.Endpoint) });
        return client.GetChatClient(modelId).AsIChatClient();
    }
}
