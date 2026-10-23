using Aether.AI.Core.Agents;
using Aether.AI.Harness.Tools;

namespace Aether.AI.Hosting.Tools;

/// <summary><see cref="IToolApprovalLevelLookup"/> 实现：转发 <see cref="ToolRegistry.LevelOf"/>。
/// 让 UI 层（Mvvm，不引 Harness）能在 AutoEdit 模式下按工具声明等级区分自动批（Confirm）与弹框（Dangerous）。</summary>
public sealed class ToolApprovalLevelLookup : IToolApprovalLevelLookup
{
    private readonly ToolRegistry _registry;
    public ToolApprovalLevelLookup(ToolRegistry registry) => _registry = registry;
    public ToolApprovalLevel LevelOf(string toolName) => _registry.LevelOf(toolName);
}
