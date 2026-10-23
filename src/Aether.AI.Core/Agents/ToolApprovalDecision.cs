namespace Aether.AI.Core.Agents;

/// <summary>
/// UI 对一次工具审批请求的决定，回传给 Harness 以驱动 MAF 协作式审批恢复轮。
/// <para>
/// <see cref="AlwaysApprove"/> 为 true 时映射到 MAF 的
/// <c>CreateAlwaysApproveToolResponse</c>（本会话对该工具免问）；否则映射到
/// <c>CreateResponse(Approved, Reason)</c>。
/// </para>
/// </summary>
public sealed record ToolApprovalDecision(
    string RequestId,
    bool Approved,
    string? Reason,
    bool AlwaysApprove);
