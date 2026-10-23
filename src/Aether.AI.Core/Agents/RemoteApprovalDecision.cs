namespace Aether.AI.Core.Agents;

/// <summary>
/// 老板手机端对一个 pending 远程审批的决策（wire DTO，桌面/手机两端共引 Core 复用同类型）。
/// <para>
/// 手机端序列化为 JSON 经 REST <c>SendMessageAsync(convId, json, "ApprovalDecision")</c> 发出；
/// 桌面 <c>SignalRDeviceChatClient</c> 收到 <c>messageType="ApprovalDecision"</c> 的消息后反序列化此类型，
/// 据此构造 <see cref="ToolApprovalDecision"/>（补上注册表里 pending 请求的真实 <c>RequestId</c>）解 TCS 恢复 run。
/// </para>
/// <para>手机只发 <c>Approved</c>/<c>Reason</c>；<c>RequestId</c> 由桌面 VM 注册表持有（按 convId 匹配单 pending）。</para>
/// </summary>
public sealed record RemoteApprovalDecision(bool Approved, string? Reason = null);
