namespace Aether.AI.Core.Agents;

/// <summary>
/// 4 档运行模式的 MAF <c>AgentMode</c> 名（单一来源）。Harness 的 <c>AetherAgentModeCatalog</c> 按这些名
/// 定义自定义模式（MAF 1.22 <c>AgentModeProviderOptions.Modes</c>）；桌面 Mvvm 的
/// <c>ChatMode.ToDesiredMode</c>、手机 Mvvm 的 desiredMode、测试断言都从这里取名——
/// <b>名字漂移 = 运行时 <c>ValidateMode</c> 抛 <see cref="ArgumentException"/></b>，故集中常量防散落硬编码。
/// <para>4 档对标 Claude Code 的 Plan/Manual/Edit-automatically/Auto：plan=交互式规划；
/// manual=逐步确认；auto-edit=可逆自动+危险审批（默认档）；auto=全自主。审批门控仍在宿主 VM 层
/// （<c>ChatApprovalDecider</c>）逐工具执行——模式指令让<b>模型感知</b>当前档位，门控不依赖模型自觉。</para>
/// </summary>
public static class AgentModes
{
    /// <summary>规划：交互式规划，先出计划不执行。</summary>
    public const string Plan = "plan";

    /// <summary>手动：每个非 Safe 工具弹审批框，模型逐步说明再动手。</summary>
    public const string Manual = "manual";

    /// <summary>自动编辑（默认）：可逆工作自主推进，危险动作走审批。</summary>
    public const string AutoEdit = "auto-edit";

    /// <summary>自动：全自主，含 Dangerous 全部自动批。</summary>
    public const string Auto = "auto";
}
