namespace Aether.AI.Core.Agents;

/// <summary>
/// 声明式响应格式（框架无关）。Harness 层映射到 MEAI 的 <c>ChatResponseFormat</c>。
/// <c>Default</c> 表示不设置，沿用 provider 默认。
/// </summary>
public enum AgentResponseFormat
{
    /// <summary>不指定，沿用模型/provider 默认。</summary>
    Default = 0,

    /// <summary>强制纯文本输出。</summary>
    Text,

    /// <summary>强制 JSON 输出（结构化输出场景）。</summary>
    Json,
}
