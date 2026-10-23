namespace Aether.AI.Core.Agents;

/// <summary>
/// 工具审批请求（框架无关 DTO）。MAF 的 <c>ToolApprovalRequestContent</c> 经 Harness
/// 映射为本类型上抛给 UI；UI 据此弹审批框，并以 <see cref="ToolApprovalDecision"/> 回馈。
/// </summary>
public sealed record ToolApprovalRequest(
    string RequestId,
    string ToolName,
    string CallId,
    Dictionary<string, object>? Input);
