namespace Aether.AI.Core.Agents;

/// <summary>工具审批等级：Safe 自动放行；Confirm 需审批；Dangerous 需审批且 UI 强调（红）。
/// 由工具适配器在注册时显式声明；AgentFactory 在 RequireToolApproval/EnableAgentModeProvider 时按级包装非 Safe 工具。
/// 与 <see cref="ToolApprovalRequest"/>/<see cref="ToolApprovalDecision"/> 同列——Core 共享契约，
/// 供 UI 层（Mvvm）经 <see cref="IToolApprovalLevelLookup"/> 查询等级实现 AutoEdit 等模式策略。</summary>
public enum ToolApprovalLevel
{
    /// <summary>只读/无副作用：自动放行，不弹审批。</summary>
    Safe = 0,
    /// <summary>有副作用/可逆：RequireToolApproval 时弹审批。</summary>
    Confirm = 1,
    /// <summary>不可逆/高风险（下单/删除/执行命令）：RequireToolApproval 时弹审批并红色强调。</summary>
    Dangerous = 2,
}
