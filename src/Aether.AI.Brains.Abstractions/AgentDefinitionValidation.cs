using Aether.AI.Core.Agents;

namespace Aether.AI.Brains.Abstractions;

/// <summary>一条定义诊断。<see cref="IsError"/> 为 true 时构建必须失败，否则仅提示会被忽略的配置。</summary>
public sealed record AgentDefinitionIssue(string Code, string Message, bool IsError = false);

/// <summary>
/// 大脑 × 定义的一致性校验。<see cref="AgentDefinition"/> 是跨大脑共享的扁平数据模型，
/// 允许把 LLM 采样参数、NL 能力开关填在规则/代码 agent 上——运行时只会静默忽略。
/// 本类把这些静默忽略显式化：真正会破坏行为的组合报错，无害但无效的配置给出告警。
/// </summary>
public static class AgentDefinitionValidation
{
    /// <summary>校验定义；<paramref name="capabilities"/> 为 null 时跳过依赖能力集的告警。</summary>
    public static IReadOnlyList<AgentDefinitionIssue> Check(
        AgentDefinition definition, BrainCapabilities? capabilities = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var issues = new List<AgentDefinitionIssue>();

        // —— 会直接改变语义的矛盾组合：必须报错 ——
        if (definition.EnableBrainRouting && definition.Engine != EngineKind.Llm)
        {
            issues.Add(new AgentDefinitionIssue(
                "RoutingRequiresLlm",
                $"Agent '{definition.Id}' sets EnableBrainRouting with Engine={definition.Engine}. " +
                "显式路由以 LLM 作未命中兜底，且固定执行 RuleProgram；它会完全接管所选大脑。" +
                "请改用 Engine=Llm，或关闭路由。",
                IsError: true));
        }

        if (definition.BackgroundAgents is { Count: > 0 } && definition.Engine != EngineKind.Llm)
        {
            issues.Add(new AgentDefinitionIssue(
                "BackgroundAgentsRequireLlm",
                $"Agent '{definition.Id}' declares BackgroundAgents but Engine={definition.Engine}. " +
                "后台子 agent 委派由 MAF 的 LLM 工具回合驱动，确定性大脑不会调用它。",
                IsError: true));
        }

        // —— 无害但会被忽略的配置：告警 ——
        if (definition.Engine == EngineKind.Rule && (definition.Rules is null || definition.Rules.Steps.Count == 0))
        {
            issues.Add(new AgentDefinitionIssue(
                "EmptyRuleProgram",
                $"Rule agent '{definition.Id}' has no rule steps; every input will return the NoMatch reply."));
        }

        if (definition.Engine == EngineKind.Llm && definition.Rules is not null && !definition.EnableBrainRouting)
        {
            issues.Add(new AgentDefinitionIssue(
                "RulesIgnoredWithoutRouting",
                $"Agent '{definition.Id}' carries a RuleProgram but EnableBrainRouting is false; the rules will be ignored. " +
                "Set EnableBrainRouting=true to use rule-first routing."));
        }

        if (capabilities is { } caps && !caps.HasFlag(BrainCapabilities.NaturalLanguageContext))
        {
            AddIfIgnored(issues, definition, "ModelId", !string.IsNullOrWhiteSpace(definition.ModelId));
            AddIfIgnored(issues, definition, "Temperature", definition.Temperature is not null);
            AddIfIgnored(issues, definition, "TopP", definition.TopP is not null);
            AddIfIgnored(issues, definition, "MaxOutputTokens", definition.MaxOutputTokens is not null);
            AddIfIgnored(issues, definition, "ResponseFormat", definition.ResponseFormat != AgentResponseFormat.Default);
            AddIfIgnored(issues, definition, "EnableWebSearch", definition.EnableWebSearch);
            AddIfIgnored(issues, definition, "EnableTodoProvider", definition.EnableTodoProvider);
            AddIfIgnored(issues, definition, "EnableAgentModeProvider", definition.EnableAgentModeProvider);
            AddIfIgnored(issues, definition, "EnableFileMemory", definition.EnableFileMemory);
            AddIfIgnored(issues, definition, "EnableLongTermMemory", definition.EnableLongTermMemory);
        }

        return issues;
    }

    /// <summary>有错误级诊断时抛出，消息一次性列出全部错误（避免逐个试错）。</summary>
    public static void ThrowIfInvalid(AgentDefinition definition, BrainCapabilities? capabilities = null)
    {
        var errors = Check(definition, capabilities).Where(issue => issue.IsError).ToArray();
        if (errors.Length == 0) return;

        throw new InvalidOperationException(
            $"Agent definition '{definition.Id}' is invalid:\n- " +
            string.Join("\n- ", errors.Select(error => $"[{error.Code}] {error.Message}")));
    }

    private static void AddIfIgnored(
        List<AgentDefinitionIssue> issues, AgentDefinition definition, string setting, bool configured)
    {
        if (!configured) return;
        issues.Add(new AgentDefinitionIssue(
            "SettingIgnoredByBrain",
            $"Agent '{definition.Id}' ({definition.Engine}) sets {setting}, which only a natural-language brain consumes; it will be ignored."));
    }
}