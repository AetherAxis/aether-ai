using Aether.AI.Core.Agents;

namespace Aether.AI.Hosting;

/// <summary>
/// Top-level configuration for the Aether.AI platform.
/// </summary>
public sealed class AetherAIOptions
{
    /// <summary>OpenAI / Azure OpenAI connection settings used to build the chat client.</summary>
    public OpenAIOptions OpenAI { get; set; } = new();

    /// <summary>
    /// When <c>true</c>, agents are built as lightweight <c>ChatClientAgent</c>s
    /// instead of the full <c>HarnessAgent</c> pipeline. Defaults to <c>false</c>.
    /// </summary>
    public bool UseChatClientAgent { get; set; } = false;

    /// <summary>
    /// HarnessAgent「文件记忆」provider 的可写根目录（每 agent 在其下建子目录隔离）。
    /// 为 null 且某 agent 启用文件记忆时，平台会静默禁用该 provider（避免默认相对路径
    /// 在只读 CWD 下抛 Read-only file system）。桌面端应设为租户数据目录下的子目录。
    /// </summary>
    public string? FileMemoryRoot { get; set; }

    /// <summary>
    /// skills 目录（每个 skill 占一个子目录：&lt;dir&gt;/&lt;name&gt;/SKILL.md，可含脚本/资源）。
    /// 配置后 AgentFactory 用 MAF 的 AgentFileSkillsSource 加载（支持 SKILL.md 正文 + 可执行脚本）。
    /// 为 null 时退回 SkillRegistry 的纯提示词 source（不支持脚本）。
    /// </summary>
    public string? SkillsDirectory { get; set; }

    /// <summary>
    /// 执行 .py skill 脚本的解释器（默认 "python3"）。从 .app 启动时 GUI 进程 PATH 受限，
    /// 建议填绝对路径（如 /usr/bin/python3 / /opt/homebrew/bin/python3）。
    /// </summary>
    public string? PythonExecutable { get; set; }

    /// <summary>
    /// Playwright 浏览器持久 profile 目录（browser_open 工具 + 网络面板共用同一 IBrowserService）。
    /// cookie/会话存盘于此——用户每个网站首次手动登录一次，后续 browser_open 自动带登录态（不用每次重登）。
    /// 为 null 时退回临时 profile（每次新开、不持久，兼容旧行为）。桌面端应设为租户数据目录下的子目录
    /// （如 &lt;tenantDir&gt;/browser-profile），不碰用户日常浏览器（独立 profile，无污染、无 profile 锁冲突）。
    /// </summary>
    public string? BrowserProfileDir { get; set; }

    /// <summary>OpenTelemetry 导出配置。配了任一项才启用 OTel SDK（接 Aether.AI.Harness + MAF 的 Activity 源）。</summary>
    public OpenTelemetryOptions OpenTelemetry { get; set; } = new();

    /// <summary>
    /// 当前宿主类型（桌面端 = DesktopOnly / 手机端 = MobileOnly）。默认 <c>All</c>（库测试/不声明时不做平台过滤）。
    /// 供 <c>ToolRegistry</c> 在运行时按工具的 <c>HostCapability</c> 声明跳过当前宿主不可用的工具；
    /// 桌面/手机宿主在 <c>aether.Configure(o =&gt; o.Host = …)</c> 里设置（早于 ToolRegistry 首次 resolve，
    /// 故走 IOptions 延迟解析安全）。
    /// </summary>
    public HostCapability Host { get; set; } = HostCapability.All;
}

/// <summary>OTel 导出选项：控制台 + OTLP（至少配一个才导出；agent 的 EnableOpenTelemetry 控制是否发 Activity）。</summary>
public sealed class OpenTelemetryOptions
{
    /// <summary>是否输出到控制台（调试用）。</summary>
    public bool ConsoleEnabled { get; set; }

    /// <summary>OTLP 导出端点（如 http://localhost:4317）；null 不导出 OTLP。</summary>
    public string? OtlpEndpoint { get; set; }
}

/// <summary>
/// Connection settings for the OpenAI-compatible chat client.
/// </summary>
public sealed class OpenAIOptions
{
    /// <summary>API key. Falls back to the OPENAI_API_KEY environment variable when empty.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>The model / deployment id to use (e.g. "gpt-4o-mini").</summary>
    public string ModelId { get; set; } = "gpt-4o-mini";

    /// <summary>Optional Azure OpenAI endpoint. When set, an AzureOpenAIClient is used.</summary>
    public string? Endpoint { get; set; }
}
