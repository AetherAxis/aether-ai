namespace Aether.AI.Harness;

/// <summary>
/// 持有聊天页当前选中的 LLM 配置 Id（全局单例）。
/// 宿主的 <c>IChatClientFactory</c> 实现桌面/手机共用 <c>LlmConfigChatClientFactory</c>）在 agent 的
/// ModelId 为空时读此值决定用哪个 LLM；聊天页 LLM 选择器变化时更新此值 + invalidate 缓存的 agent，下轮 run 重建时用新 LLM。
/// 实现 agent 与 LLM 解耦：agent 配置不含模型，模型在对话时选。
/// </summary>
public sealed class SelectedLlmHolder
{
    private string? _llmConfigId;
    public string? LlmConfigId
    {
        get => Volatile.Read(ref _llmConfigId);
        set => Volatile.Write(ref _llmConfigId, value);
    }
}
