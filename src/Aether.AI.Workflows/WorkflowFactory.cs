using Aether.AI.Core.Workflows;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace Aether.AI.Workflows;

/// <summary>
/// 把声明式 <see cref="WorkflowDefinition"/> + 参与者 <see cref="AIAgent"/> 翻译成 MAF Handoff workflow，
/// 并可包成 <see cref="AIAgent"/>（<c>AsAIAgent</c> + <c>InProcessExecutionEnvironment</c>），
/// 供 HarnessRuntime/adapter 以"单个 agent"方式流式运行（复用 ToolStreamEvent 提取与分段展示）。
/// 仅依赖 AIAgent/Core，不依赖 Aether.AI.Harness。
/// </summary>
public static class WorkflowFactory
{
    /// <summary>构建 MAF Handoff <see cref="Workflow"/>。</summary>
    public static Workflow Build(WorkflowDefinition def, IReadOnlyDictionary<Guid, AIAgent> participants)
    {
        if (def is null) throw new ArgumentNullException(nameof(def));
        if (participants is null) throw new ArgumentNullException(nameof(participants));

        if (!participants.TryGetValue(def.EntryAgentId, out var entry))
            throw new InvalidOperationException($"入口 agent 未在参与者中提供：{def.EntryAgentId}");

        var builder = new HandoffWorkflowBuilder(entry);

        // 全部参与者登记（入口已作为起始，AddParticipants 含其余）
        builder.AddParticipants(participants.Values);

        foreach (var h in def.Handoffs)
        {
            if (!participants.TryGetValue(h.FromAgentId, out var from))
                throw new InvalidOperationException($"转交边 from agent 未提供：{h.FromAgentId}");
            if (!participants.TryGetValue(h.ToAgentId, out var to))
                throw new InvalidOperationException($"转交边 to agent 未提供：{h.ToAgentId}");
            builder.WithHandoff(from, to, h.Reason);
        }

        if (!string.IsNullOrWhiteSpace(def.HandoffInstructions))
            builder.WithHandoffInstructions(def.HandoffInstructions!);

        return builder.Build();
    }

    /// <summary>构建 workflow 并包成 <see cref="AIAgent"/>（进程内执行环境）。</summary>
    public static AIAgent BuildAsAIAgent(WorkflowDefinition def, IReadOnlyDictionary<Guid, AIAgent> participants,
        string id, string name, string description)
    {
        var workflow = Build(def, participants);
        return workflow.AsAIAgent(id, name, description, InProcessExecution.Default);
    }
}
