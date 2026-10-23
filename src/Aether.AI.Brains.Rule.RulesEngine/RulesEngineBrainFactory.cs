using Aether.AI.Brains.Abstractions;
using Aether.AI.Core.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Aether.AI.Brains.Rule.RulesEngine;

/// <summary>
/// <c>rulesEngine</c> 规则后端：把 <see cref="AgentDefinition.RuleSource"/>（workflow JSON 数组）交给
/// Microsoft RulesEngine 求值。与内置 <c>program</c> 后端同属 <see cref="EngineKind.Rule"/>，
/// 按 <see cref="AgentDefinition.BrainBackend"/> 选择。
/// </summary>
public sealed class RulesEngineBrainFactory : IContentBrainFactory
{
    private readonly RulesEngineRuleOptions _options;
    private readonly ILoggerFactory? _loggerFactory;

    public RulesEngineBrainFactory(RulesEngineRuleOptions? options = null, ILoggerFactory? loggerFactory = null)
    {
        _options = options ?? new RulesEngineRuleOptions();
        _loggerFactory = loggerFactory;
    }

    public EngineKind Kind => EngineKind.Rule;

    public string BackendId => "rulesEngine";

    public BrainCapabilities Capabilities => BrainCapabilities.Tools;

    public bool CanCreate(AgentDefinition definition) => definition.Engine == EngineKind.Rule;

    public IBrain Create(AgentDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.RuleSource))
        {
            throw new InvalidOperationException(
                "RulesEngine 后端需要 AgentDefinition.RuleSource（workflow JSON 数组）；" +
                "内置 program 后端用 Rules(RuleProgram)，二者不能混用。");
        }

        return new RulesEngineBrain(
            definition.RuleSource,
            _options,
            _loggerFactory?.CreateLogger<RulesEngineBrain>());
    }

    public IChatClient CreateChatClient(AgentDefinition definition)
        => new BrainChatClient(Create(definition), _loggerFactory?.CreateLogger<BrainChatClient>());
}

public static class RulesEngineBrainServiceCollectionExtensions
{
    /// <summary>
    /// 注册 <c>rulesEngine</c> 规则后端。与 <c>AddRuleBrains()</c> 同时使用时，每个 Rule agent
    /// 必须用 <c>BrainBackend("rulesEngine")</c> 或 <c>BrainBackend("program")</c> 显式选择。
    /// </summary>
    public static IServiceCollection AddRulesEngineRuleBrains(
        this IServiceCollection services,
        Action<RulesEngineRuleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new RulesEngineRuleOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IBrainFactory, RulesEngineBrainFactory>());
        return services;
    }
}
