using System.Diagnostics;

namespace Aether.AI.Harness.Telemetry;

/// <summary>
/// Aether agent 运行时遥测的 <see cref="ActivitySource"/>。当专家 <c>EnableOpenTelemetry</c> 开启时，
/// <c>RunStreamingCoreAsync</c> 围每轮 run 起 <c>agent.run</c> Activity（tag: agent/conversation/mode +
/// 事件: tool call/result、approval、error、usage）。无 listener 时 StartActivity 返回 null（cheap no-op）。
/// 宿主经 <c>AddOpenTelemetry().WithTracing(t => t.AddSource("Aether.AI.Harness"))</c> 接导出器。
/// </summary>
internal static class AetherActivitySource
{
    public const string Name = "Aether.AI.Harness";
    public static readonly ActivitySource Instance = new(Name, "1.0");

    public const string ActivityAgentRun = "agent.run";
    public const string TagAgentId = "aether.agent.id";
    public const string TagConversationId = "aether.conversation.id";
    public const string TagMode = "aether.agent.mode";
    public const string TagStatus = "aether.run.status";
    public const string EventToolCall = "tool.call";
    public const string EventToolResult = "tool.result";
    public const string EventApproval = "tool.approval";
    public const string EventError = "run.error";
    public const string EventUsage = "run.usage";
}
