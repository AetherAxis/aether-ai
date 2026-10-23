using System.Text.Json;
using Aether.AI.Core.Agents;

namespace Aether.AI.Core.Conversations.Runs;

/// <summary>
/// <see cref="AgentEvent.Payload"/> 的 typed 解释。每种 <see cref="AgentEventType"/> 对应一个 record，
/// 字段直接复用 <see cref="ToolStreamEvent"/>/<see cref="ToolApprovalRequest"/>/<see cref="TodoItemInfo"/> 的形状。
/// <para>
/// 序列化经 <see cref="Serialize"/>/<see cref="Deserialize{T}"/>，统一 <c>JsonSerializerOptions</c>。
/// 存储为紧凑 JSON（不缩进）；<c>Dictionary&lt;string,object&gt;</c> 值来自 MEAI 已是 JSON 友好类型。
/// </para>
/// </summary>
public static class AgentEventPayload
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public sealed record TextChunk(string Text);

    public sealed record ReasoningChunk(string Text);

    /// <param name="CallId">MEAI FunctionCallContent.CallId——写入时即有，永不丢。</param>
    public sealed record ToolCall(string CallId, string Name, Dictionary<string, object>? Input);

    public sealed record ToolResult(string CallId, string Name, Dictionary<string, object>? Output);

    /// <param name="Todos">当前待办列表快照（二元完成状态）。</param>
    public sealed record PlanUpdated(IReadOnlyList<TodoItemInfo> Todos);

    public sealed record ApprovalRequested(string RequestId, string CallId, string Name, Dictionary<string, object>? Input);

    public sealed record ApprovalDecision(string RequestId, bool Approved, string? Reason, bool AlwaysApprove);

    public sealed record Error(string Message);

    public sealed record Usage(string Summary);

    /// <summary>序列化一个 payload record 为紧凑 JSON。</summary>
    public static string Serialize(object? payload) =>
        payload is null ? string.Empty : JsonSerializer.Serialize(payload, JsonOpts);

    /// <summary>反序列化 payload JSON 为指定 record 类型。</summary>
    public static T? Deserialize<T>(string json) =>
        string.IsNullOrEmpty(json) ? default : JsonSerializer.Deserialize<T>(json, JsonOpts);
}
