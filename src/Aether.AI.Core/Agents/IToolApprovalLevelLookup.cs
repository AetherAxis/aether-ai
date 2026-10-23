namespace Aether.AI.Core.Agents;

/// <summary>按工具名查其声明审批等级（<see cref="ToolRegistry.LevelOf"/> 的 Core 抽象）。
/// UI 层（Mvvm，不引 Harness）经此接口在 AutoEdit 模式下区分 Confirm（自动批）与 Dangerous（弹框）。
/// 实现在 Hosting（注入 ToolRegistry）。未注册工具回退 Safe。</summary>
public interface IToolApprovalLevelLookup
{
    ToolApprovalLevel LevelOf(string toolName);
}
