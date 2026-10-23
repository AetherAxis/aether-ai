using Aether.AI.Brains.Abstractions;
using Aether.AI.Core.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aether.AI.Brains.Routing;

public sealed class BrainRoutingOptions
{
    public int RulePriority { get; set; } = 0;
}

public sealed class RuleFirstRoutingClientFactory : IBrainRoutingClientFactory
{
    private readonly IBrainFactoryRegistry _brains;
    private readonly IChatClientFactory? _llmFactory;
    private readonly BrainRoutingOptions _options;
    private readonly ILoggerFactory? _loggerFactory;

    public RuleFirstRoutingClientFactory(
        IBrainFactoryRegistry brains,
        BrainRoutingOptions options,
        ILoggerFactory? loggerFactory = null,
        IChatClientFactory? llmFactory = null)
    {
        _brains = brains;
        _options = options;
        _loggerFactory = loggerFactory;
        _llmFactory = llmFactory;
    }

    public IChatClient Create(AgentDefinition definition)
    {
        var ruleDefinition = CloneForRule(definition);
        if (_brains.Resolve(ruleDefinition) is not IContentBrainFactory ruleFactory)
        {
            throw new InvalidOperationException(
                "Rule-first routing requires a registered content brain (call AddRuleBrains()).");
        }
        var ruleBrain = ruleFactory.Create(ruleDefinition);
        return new RuleFirstRoutingChatClient(
            ruleDefinition,
            new[] { new RuleProgramMatcher(definition.Id, _options.RulePriority) },
            ruleBrain,
            () => _llmFactory?.Create(definition.ModelId),
            _loggerFactory?.CreateLogger<RuleFirstRoutingChatClient>());
    }

    private static AgentDefinition CloneForRule(AgentDefinition source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Engine = EngineKind.Rule,
        // 路由内部的规则分支必须与宿主选择的规则后端一致；否则多后端注册时克隆体会因未指定后端而歧义。
        BrainBackend = source.BrainBackend,
        Rules = source.Rules,
        RuleSource = source.RuleSource,
        Instructions = source.Instructions,
        ModelId = source.ModelId,
        Tools = source.Tools,
        ToolApprovals = source.ToolApprovals,
        AutoApprovalRules = source.AutoApprovalRules,
        Memory = source.Memory,
        MaxHistory = source.MaxHistory,
        RequireToolApproval = source.RequireToolApproval,
        EnableAgentModeProvider = source.EnableAgentModeProvider,
        EnableOpenTelemetry = source.EnableOpenTelemetry,
    };
}

public static class BrainRoutingServiceCollectionExtensions
{
    public static IServiceCollection AddBrainRouting(
        this IServiceCollection services,
        Action<BrainRoutingOptions>? configure = null)
    {
        var options = new BrainRoutingOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton<IBrainRoutingClientFactory, RuleFirstRoutingClientFactory>();
        return services;
    }
}
