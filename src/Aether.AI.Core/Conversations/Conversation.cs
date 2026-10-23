namespace Aether.AI.Core.Conversations;

/// <summary>
/// A persisted conversation: metadata for an ordered sequence of agent runs. The full
/// transcript lives as typed <see cref="Runs.AgentEvent"/>s attached to <see cref="Runs.AgentRun"/>s
/// (loaded on demand via <c>IConversationStore.GetRunsAsync</c>/<c>GetEventsAsync</c>), NOT as a
/// flat message list on this object.
/// <para>
/// <see cref="LastMessagePreview"/>/<see cref="LastMessageAt"/> are denormalized for list display
/// (会话历史 flyout) so the summary query need not join the run/event tables.
/// </para>
/// </summary>
public sealed class Conversation
{
    public Guid ConversationId { get; init; } = Guid.NewGuid();

    public Guid? SessionId { get; init; }

    public string? UserId { get; init; }

    /// <summary>The logical id of the agent bound to this conversation (e.g. a custom agent's Guid).</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>Display name of the agent bound to this conversation.</summary>
    public string AgentName { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>最近一条消息的预览文本（会话列表展示用），由 store 在 append run 时反范式化更新。</summary>
    public string? LastMessagePreview { get; set; }

    /// <summary>最近一条消息的时间（会话列表排序/展示用），由 store 在 append run 时反范式化更新。</summary>
    public DateTimeOffset? LastMessageAt { get; set; }
}
