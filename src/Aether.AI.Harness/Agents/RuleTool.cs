using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// 把 <see cref="RuleRegistry"/> 里的规则程序暴露为单个 <c>run_rule</c> <see cref="AIFunction"/>。
/// LLM agent 调一次 <c>run_rule(name, input)</c> → <see cref="RuleToolExecutor"/> 内部跑完整条确定性链
/// → 返回最终文本。中间的工具调用/原始结果对 LLM 不可见（省 token：省决策推理 + 省原始结果 + 缩小 LLM
/// 工具 schema 体积——业务工具被 executor 在宿主侧调用,不进 LLM 的工具列表）。
/// <para>工具描述自动枚举已注册规则及其描述，让 LLM 知道可调哪些规则。</para>
/// </summary>
public static class RuleTool
{
    public const string ToolName = "run_rule";

    /// <summary>
    /// 构建 <c>run_rule</c> 工具。
    /// </summary>
    /// <param name="registry">规则程序注册表（按名查找）。</param>
    /// <param name="toolResolver">工具名 → <see cref="AIFunction"/> 解析器。宿主接 <c>ToolRegistry</c>，
    /// 让 executor 能在宿主侧调用业务工具（对 LLM 隐藏）。</param>
    public static AIFunction Create(RuleRegistry registry, Func<string, AIFunction?> toolResolver)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(toolResolver);

        // MEAI AIFunctionFactory 把 CancellationToken 末参识别为注入参数——不暴露进 JSON schema，
        // 挂 agent 运行时的取消令牌（对齐全仓其它工具）。旧实现传 CancellationToken.None：规则内部跑的
        // 业务工具（executor 侧 InvokeToolAsync → AIFunction.InvokeAsync）也全部拿到 None，
        // agent 中断/超时后规则链仍继续跑业务工具。
        Func<string, string?, CancellationToken, Task<string>> invoke = async (name, input, cancellationToken) =>
        {
            if (!registry.TryGet(name, out var entry))
            {
                return $"规则不存在: {name}";
            }
            var executor = new RuleToolExecutor(new RuleProgramBrain(entry.Program), toolResolver);
            return await executor.RunAsync(input, cancellationToken).ConfigureAwait(false);
        };

        return AIFunctionFactory.Create(invoke, ToolName, BuildDescription(registry));
    }

    private static string BuildDescription(RuleRegistry registry)
    {
        var rules = registry.GetAll();
        if (rules.Count == 0)
        {
            return "运行一个已注册的规则程序（确定性，无 LLM，适合机械的数据查询/工具编排）。当前无已注册规则。";
        }
        var list = string.Join("\n", rules.Select(r => $"- {r.Name}: {r.Description}"));
        return "运行一个已注册的规则程序（确定性，无 LLM，适合机械的数据查询/工具编排）。"
            + "参数 name=规则名，input=要处理的查询/数据。可用规则:\n" + list;
    }
}
