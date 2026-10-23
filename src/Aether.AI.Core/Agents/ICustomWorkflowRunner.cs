namespace Aether.AI.Core.Agents;

/// <summary>
/// 自定义 Workflow 运行器：把目录中的 CustomWorkflowDefinition 建成 MAF Handoff workflow
/// （参与者用 AgentFactory 建成 AIAgent），包成单个 AIAgent 后经 HarnessRuntime 流式运行。
/// 每个 conversationId 拥有独立会话（多轮记忆）；workflow 编辑后调 <see cref="Invalidate"/> 重建。
/// 与 <see cref="ICustomAgentRunner"/> 同构（同样的流式回调），聊天页可统一 dispatch。
/// 契约住 Core（与它用的 <see cref="StreamHandlers"/>/<see cref="ToolApprovalDecision"/> 同居），
/// impl 在 ai/Agents 适配器。
/// </summary>
public interface ICustomWorkflowRunner
{
    Task RunStreamingAsync(
        Guid conversationId,
        Guid workflowId,
        string message,
        StreamHandlers? handlers = null,
        ToolApprovalDecision? approvalResponse = null,
        string? desiredMode = null,
        CancellationToken cancellationToken = default);

    /// <summary>读取当前会话的 AgentMode（plan/execute…）；workflow 未启用 AgentModeProvider 返回 null。</summary>
    Task<string?> GetModeAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>设置当前会话的 AgentMode；未启用返回 false。</summary>
    Task<bool> SetModeAsync(Guid conversationId, string mode, CancellationToken cancellationToken = default);

    /// <summary>使该 workflow 的缓存会话失效（编辑/删除后调用）。</summary>
    void Invalidate(Guid workflowId);

    /// <summary>
    /// 会话删除时清出该 conversationId 的运行时会话：丢弃 (workflowAIAgent, FrameworkAgentSession) 缓存。
    /// 工作流 session 自管于本 runner（不经 HarnessRuntime._frameworkSessions），TryRemove 即切断其唯一强引用
    /// → RestoringChatHistoryProvider 弱键缓存随 GC 自动回收（防长会话累积内存泄漏）。
    /// </summary>
    void EvictConversation(Guid conversationId);
}
