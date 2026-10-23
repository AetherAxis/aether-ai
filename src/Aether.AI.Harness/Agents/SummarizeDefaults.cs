namespace Aether.AI.Harness.Agents;

/// <summary>
/// <see cref="AgentMemoryMode.Summarize"/> 记忆模式的默认参数。Summarize 用 MEAI
/// <c>SummarizingChatReducer</c> 把超出保留窗口的旧历史 LLM 压缩成一段摘要，避免滑动窗口直接丢弃
/// 丢失关键事实。此常量提供中文默认摘要指令，由 Hosting 的 history-provider 工厂注入 reducer 时使用。
/// </summary>
public static class SummarizeDefaults
{
    /// <summary>
    /// 默认摘要提示词：要求模型用简洁中文总结对话历史的关键信息、已确认事实、用户意图与未完成任务，
    /// 供后续对话延续上下文；只输出摘要、不增新信息。
    /// </summary>
    public const string Prompt =
        "你是对话摘要助手。请用简洁中文总结以下对话历史的关键信息、已确认事实、用户意图与未完成任务，" +
        "供后续对话延续上下文。只输出摘要，勿增新信息。";
}
