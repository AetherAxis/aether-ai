using System.Diagnostics.CodeAnalysis;
using Aether.AI.Core.Agents;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// 规则程序注册表：name → <see cref="RuleProgram"/>（+ 描述），供 <see cref="RuleTool"/> 的 <c>run_rule</c>
/// 工具按名分发。镜像 <c>SkillRegistry</c>/<c>ToolRegistry</c> 的静态注册模式。规则程序可同时以 agent 形态
/// （<see cref="RuleChatClient"/>）和工具形态（<see cref="RuleToolExecutor"/>）消费——注册一份,两种 facade 共用。
/// </summary>
public sealed class RuleRegistry
{
    private readonly Dictionary<string, RuleProgramEntry> _byName = new(StringComparer.Ordinal);

    /// <summary>注册/覆盖一个规则程序。description 缺省回落到程序名。</summary>
    public void Register(string name, RuleProgram program, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("规则名不能为空", nameof(name));
        }
        ArgumentNullException.ThrowIfNull(program);
        _byName[name] = new RuleProgramEntry(name, program, description ?? program.Name ?? name);
    }

    public bool TryGet(string name, [NotNullWhen(true)] out RuleProgramEntry? entry)
        => _byName.TryGetValue(name, out entry);

    /// <summary>所有已注册规则（供工具描述枚举给 LLM）。</summary>
    public IReadOnlyList<RuleProgramEntry> GetAll() => _byName.Values.ToList();

    /// <summary>一条注册项：程序名 + 程序 + 给 LLM 的描述。</summary>
    public sealed record RuleProgramEntry(string Name, RuleProgram Program, string Description);
}
