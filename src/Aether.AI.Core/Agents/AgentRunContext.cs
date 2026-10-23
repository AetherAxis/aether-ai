namespace Aether.AI.Core.Agents;

/// <summary>
/// 运行期 ambient 上下文：让 AI 工具在 <see cref="System.Threading.AsyncLocal{T}"/> 上读取「当前正在执行的
/// agent / conversation」标识。由 <c>HarnessRuntime.RunStreamingCoreAsync</c> 在每轮运行起止处设置/清理
/// （<c>try/finally</c> 保证并发 run 不串扰）。供宿主层工具（如勾选工具进当前 agent、重新召唤当前 agent）
/// 精准定位「谁在调自己」，而无须走「全局失效」或新增会话反查 seam。
/// </summary>
/// <remarks>
/// 值类型用 <see cref="string"/>：<see cref="AgentDefinition.Id"/> / <c>AgentSession.AgentId</c> 均为 string
/// （Guid.ToString()），与 <c>ICustomAgentRunner.Invalidate(Guid)</c> 之间由工具自行 <c>Guid.Parse</c>。
/// 类名特意避开 MAF 的 <c>Microsoft.Agents.AI.AgentRunContext</c>（Harness 引两命名空间会二义）。
/// </remarks>
public static class AgentExecutionContext
{
    /// <summary>当前正在执行工具的 agent 的 Id（string，Guid 格式）。未在 run 内为 null。</summary>
    public static AsyncLocal<string?> CurrentAgentId { get; } = new();

    /// <summary>当前 run 所属会话（conversationId，string，Guid 格式）。未在 run 内为 null。</summary>
    public static AsyncLocal<string?> CurrentConversationId { get; } = new();
}
