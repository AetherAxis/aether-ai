namespace Aether.AI.Core.Conversations.Runs;

/// <summary>
/// 跨会话按时段查询的 run 轻量投影（<see cref="IConversationStore.GetRunsInRangeAsync"/> 返回）。
/// 带 <see cref="ConversationTitle"/> 便于活动热力图详情直接显示，无需再回查会话元数据。
/// <see cref="StartedAt"/>/<see cref="FinishedAt"/> 与 <see cref="AgentRun"/> 同为 <see cref="DateTimeOffset"/>。
/// </summary>
public sealed record RunSummary(
    Guid RunId,
    Guid ConversationId,
    string ConversationTitle,
    string AgentId,
    string AgentName,
    RunStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string UserMessage);
