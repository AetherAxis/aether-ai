using Aether.AI.Harness.Agents;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.AI.Harness.Scripting;

public static class RuleScriptingExtensions
{
    /// <summary>
    /// 注册代码规则脚本加载服务（扫描 CodeRule 笔记 → Roslyn 编译 → ICodeBrainRegistry.Register）。
    /// 同时注册 <see cref="ICodeBrainRegistry"/> 单例——<see cref="Aether.AI.Harness.Agents.AgentFactory"/>
    /// 经 DI 取它按笔记 Id 解析代码大脑。须在 <c>AddAetherAI</c>/<c>AddDesktopCommon</c> 之后调用。
    /// </summary>
    public static IServiceCollection AddRuleScripting(this IServiceCollection services)
    {
        services.AddSingleton<ICodeBrainRegistry, CodeBrainRegistry>();
        services.AddSingleton<IRuleScriptingService, RuleScriptingService>();
        return services;
    }
}
