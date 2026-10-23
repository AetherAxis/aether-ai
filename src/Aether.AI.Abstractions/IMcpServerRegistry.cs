using Microsoft.Extensions.AI;

namespace Aether.AI.Abstractions;

/// <summary>
/// MCP server 注册表的稳定抽象（定义在 Abstractions，避免 shell/宿主/business 引用 alpha 版的 <c>Aether.AI.Mcp</c>）。
/// 具体实现 <c>Aether.AI.Mcp.McpServerRegistry</c> 在 <c>Aether.AI.Mcp</c> 项目（opt-in）。
/// <para>宿主在启动时调用 <see cref="StartAsync"/> 连接各 server 并预解析工具；
/// <see cref="GetTools"/> 供 <c>AgentFactory</c>（经 <c>Func&lt;IReadOnlyList&lt;AIFunction&gt;&gt;</c>）取用。</para>
/// </summary>
public interface IMcpServerRegistry
{
    /// <summary>是否已连接并预解析工具。</summary>
    bool IsStarted { get; }

    /// <summary>连接所有已配置 MCP server 并预解析工具。幂等。</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>重读目录并重连所有 server（增删改配置后调用）。</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>已解析的 MCP 工具（StartAsync 前为空）。</summary>
    IReadOnlyList<AIFunction> GetTools();

    /// <summary>各 server 的连接结果（配置 + 状态 + 工具 + 错误），供管理 UI 展示。</summary>
    IReadOnlyList<McpServerEntry> GetServers();

    /// <summary>健康检查：探活每个已连接 server，任何掉线则触发 RefreshAsync 重连。</summary>
    Task CheckHealthAsync(CancellationToken cancellationToken = default);
}
