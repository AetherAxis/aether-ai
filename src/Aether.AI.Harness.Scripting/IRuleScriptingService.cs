namespace Aether.AI.Harness.Scripting;

/// <summary>
/// 代码规则脚本加载服务：扫描 CodeRule 笔记 → Roslyn 编译 → 注册到 <c>ICodeBrainRegistry</c>。
/// 对称于 Trading 的 <c>IStrategyScriptingService</c>。
/// </summary>
public interface IRuleScriptingService
{
    /// <summary>扫描代码规则笔记并编译注册。<c>Id</c> 作注册表键（与 <c>AgentDefinition.Id</c> 一致）。</summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>触发防抖重加载（500ms）。host 订阅 NotesChanged 后调用。</summary>
    void Reload();

    /// <summary>编译失败的笔记 Id→错误（供 UI/log 查看；成功的不在内）。</summary>
    IReadOnlyDictionary<string, string> LoadErrors { get; }

    /// <summary>代码规则脚本加载完成（注册表已更新）时触发。</summary>
    event Action? RulesLoaded;
}
