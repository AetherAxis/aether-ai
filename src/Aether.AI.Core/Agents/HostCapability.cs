namespace Aether.AI.Core.Agents;

/// <summary>工具的宿主能力归属：哪些宿主（桌面/手机）能实际执行该工具。
/// 与 <see cref="ToolApprovalLevel"/> 同列——Core 共享契约，供 UI 层（Mvvm）在工具选择器里
/// 置灰不可用工具 + 供 <c>ToolRegistry.ResolveAll</c> 运行时兜底（跳过当前宿主不可用的工具）。
/// 由工具适配器在注册时显式声明；默认 <see cref="All"/>（不声明即两端通用，绝大多数工具）。
/// 平台差异是「工具的宿主可达性」，不是「代码放哪个目录」——<c>ai/</c> 目录按业务上下文组织，
/// 平台归属由本标记声明、由宿主层的注册/DI 落地。</summary>
public enum HostCapability
{
    /// <summary>两端通用（默认）：桌面端 + 手机端都能执行。</summary>
    All = 0,
    /// <summary>仅桌面端（依赖 CTP 原生库 / FlaUI / Lean 引擎 / 弹出桌面 UI 等）：如期货交易、股票下单、回测、终端、浏览器。</summary>
    DesktopOnly = 1,
    /// <summary>仅手机端（依赖 MAUI 原生交互等）。</summary>
    MobileOnly = 2,
}
