using Aether.AI.Brains.Abstractions;
using Aether.AI.Core.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Aether.AI.Brains.Rule.NRules;

/// <summary>
/// <c>nRules</c> 规则后端：把 <see cref="AgentDefinition.RuleSource"/> 声明的 NRules 规则类
/// 编译成会话工厂并求值。与内置 <c>program</c>、<c>rulesEngine</c> 同属 <see cref="EngineKind.Rule"/>，
/// 按 <see cref="AgentDefinition.BrainBackend"/> 选择。
/// </summary>
public sealed class NRulesBrainFactory : IContentBrainFactory
{
    private readonly NRulesRuleOptions _options;
    private readonly ILoggerFactory? _loggerFactory;

    public NRulesBrainFactory(NRulesRuleOptions? options = null, ILoggerFactory? loggerFactory = null)
    {
        _options = options ?? new NRulesRuleOptions();
        _loggerFactory = loggerFactory;
    }

    public EngineKind Kind => EngineKind.Rule;

    public string BackendId => "nRules";

    public BrainCapabilities Capabilities => BrainCapabilities.Tools;

    public bool CanCreate(AgentDefinition definition) => definition.Engine == EngineKind.Rule;

    public IBrain Create(AgentDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.RuleSource))
        {
            throw new InvalidOperationException(
                "NRules 后端需要 AgentDefinition.RuleSource（{\"ruleTypes\":[...]} 或 {\"ruleAssemblies\":[...]}）；" +
                "内置 program 后端用 Rules(RuleProgram)，rulesEngine 后端用 workflow JSON。");
        }

        return new NRulesBrain(
            definition.RuleSource,
            _options,
            _loggerFactory?.CreateLogger<NRulesBrain>());
    }

    public IChatClient CreateChatClient(AgentDefinition definition)
        => new BrainChatClient(Create(definition), _loggerFactory?.CreateLogger<BrainChatClient>());
}

public static class NRulesBrainServiceCollectionExtensions
{
    /// <summary>
    /// 注册 <c>nRules</c> 规则后端。与 <c>AddRuleBrains()</c> / <c>AddRulesEngineRuleBrains()</c>
    /// 同时使用时，每个 Rule agent 必须用 <c>BrainBackend(...)</c> 显式选择。
    /// </summary>
    public static IServiceCollection AddNRulesRuleBrains(
        this IServiceCollection services,
        Action<NRulesRuleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new NRulesRuleOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IBrainFactory, NRulesBrainFactory>());
        return services;
    }
}
