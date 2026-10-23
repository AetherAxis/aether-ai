using System.Collections.Concurrent;
using Aether.AI.Brains.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using global::NRules;
using global::NRules.Fluent;

namespace Aether.AI.Brains.Rule.NRules;

/// <summary>
/// 把 NRules 接成 <see cref="IBrain"/> 的适配器。
/// <para>职责边界：NRules 负责 Rete 匹配、议程与冲突消解；适配器负责把规则声明的动作
/// （<see cref="NRulesDecisionContext.Reply"/> / <see cref="NRulesDecisionContext.CallTool"/>）翻译成
/// <see cref="AIContent"/>。工具执行、结果回填、多步循环仍由 MAF/Harness 负责。</para>
/// <para>会话策略：NRules 的 <see cref="ISession"/> 是事实网络运行态，而 <see cref="BrainContext"/>
/// 每个决策步骤都可完整重建。适配器因此每步插入新事实、新建会话并 <see cref="ISession.Fire(CancellationToken)"/>，
/// 规则编译（<see cref="ISessionFactory"/>）只在构造时做一次——与 <c>RuleProgramBrain</c> 一样保持无状态，
/// 不需要按会话保活。</para>
/// </summary>
public sealed class NRulesBrain : IBrain
{
    private readonly ISessionFactory _sessionFactory;
    private readonly NRulesRuleOptions _options;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, byte> _warned = new(StringComparer.Ordinal);

    public NRulesBrain(string ruleSource, NRulesRuleOptions? options = null, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(ruleSource))
        {
            throw new ArgumentException("NRules rule source is required.", nameof(ruleSource));
        }

        _options = options ?? new NRulesRuleOptions();
        _logger = logger;

        var (types, assemblies) = NRulesRuleSet.Resolve(ruleSource);

        var repository = new RuleRepository();
        if (types.Count > 0)
        {
            repository.Load(spec => spec.From(types.ToArray()));
        }
        if (assemblies.Count > 0)
        {
            repository.Load(spec => spec.From(assemblies.ToArray()));
        }

        _sessionFactory = repository.Compile();
    }

    public ValueTask<IReadOnlyList<AIContent>> DecideAsync(BrainContext context, CancellationToken cancellationToken)
    {
        // 安全闸：本轮工具调用数超限 → 强制终止，防死循环（与其他规则后端对齐）。
        if (context.ToolCallsThisTurn >= _options.MaxSteps)
        {
            return new(new AIContent[] { new TextContent($"规则步数超限（已 {_options.MaxSteps} 步）") });
        }

        var decision = new NRulesDecisionContext(context);
        var session = _sessionFactory.CreateSession();
        session.Insert(decision);
        session.Fire(cancellationToken);

        if (decision.Emissions > 1)
        {
            WarnMultipleEmissions(decision.Emissions);
        }

        _logger?.LogDebug("NRules fired at step {Step}; emissions={Emissions}.", context.ToolCallsThisTurn, decision.Emissions);
        return new(decision.ToContents(_options.NoMatchReply));
    }

    private void WarnMultipleEmissions(int emissions)
    {
        if (!_warned.TryAdd("multiple", 0))
        {
            return;
        }

        _logger?.LogWarning(
            "NRules rules emitted {Count} actions in one step; only the first was used. " +
            "Use guards (e.g. ToolCallCount) so at most one rule declares an action per step.",
            emissions);
    }
}
