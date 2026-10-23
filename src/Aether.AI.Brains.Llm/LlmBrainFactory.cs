using Aether.AI.Brains.Abstractions;
using Aether.AI.Core.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aether.AI.Brains.Llm;

/// <summary>
/// LLM 也是「大脑」：与其他大脑一样经 <see cref="IBrainFactory"/> 注册、由 Harness 统一解析，
/// 区别只在于它的传输就是模型客户端本身（不需要 <see cref="IBrain"/> 适配器），
/// 且能力集包含 <see cref="BrainCapabilities.NaturalLanguageContext"/> 与
/// <see cref="BrainCapabilities.Sampling"/>——Harness 据此决定是否挂载 NL provider。
/// </summary>
public sealed class LlmBrainFactory : IBrainFactory
{
    private readonly IChatClientFactory _chatClientFactory;

    public LlmBrainFactory(IChatClientFactory chatClientFactory)
        => _chatClientFactory = chatClientFactory ?? throw new ArgumentNullException(nameof(chatClientFactory));

    public EngineKind Kind => EngineKind.Llm;
    public BrainCapabilities Capabilities => BrainCapabilitiesDefaults.Llm;

    public bool CanCreate(AgentDefinition definition) => definition.Engine == EngineKind.Llm;

    public IChatClient CreateChatClient(AgentDefinition definition)
        => _chatClientFactory.Create(definition.ModelId);
}

public static class LlmBrainRegistrationExtensions
{
    /// <summary>把 LLM 大脑注册进统一大脑注册表（与 Rule/Code 同一接缝）。</summary>
    public static IServiceCollection AddLlmBrainFactory(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IBrainFactory, LlmBrainFactory>());
        return services;
    }
}