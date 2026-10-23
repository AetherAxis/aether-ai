using Aether.AI.Abstractions;
using Aether.AI.Hosting;
using Aether.AI.Harness.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Aether.AI.Mcp;

/// <summary>
/// <see cref="IMcpManagementService"/> 实现：读写 <c>mcp-servers/&lt;name&gt;.json</c>（主流 mcpServers 映射格式）
/// + 经 <see cref="IMcpServerRegistry"/> 重连 + 映射 <see cref="McpServerEntry"/> → <see cref="McpServerStatus"/> DTO。
/// <see cref="EnsureStartedAsync"/> 连接后把工具注册进 <see cref="ToolRegistry"/>（供 AgentFactory 解析）。
/// </summary>
public sealed class McpManagementService : IMcpManagementService
{
    public event EventHandler? Changed;

    private readonly IMcpServerRegistry _registry;
    private readonly ToolRegistry? _toolRegistry;
    private readonly string _directory;
    private readonly ILoggerFactory? _loggerFactory;

    public McpManagementService(IMcpServerRegistry registry, McpManagementServiceOptions options,
        ToolRegistry? toolRegistry = null, ILoggerFactory? loggerFactory = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _directory = options?.Directory ?? throw new ArgumentNullException(nameof(options));
        _toolRegistry = toolRegistry;
        _loggerFactory = loggerFactory;
    }

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        await _registry.StartAsync(cancellationToken).ConfigureAwait(false);
        // 连接完成后把工具注册进 ToolRegistry（TryAdd 幂等，多次调用安全）。
        if (_toolRegistry is not null)
        {
            var tools = _registry.GetTools().ToList();
            foreach (var t in tools) _toolRegistry.RegisterMcpTool(t);
            // 重连后清理已不存在的 MCP 工具（删除/改名/换 server 的残留会指向已 dispose 的 client）。
            _toolRegistry.SyncMcpTools(tools.Select(t => t.Name));
        }
    }

    public Task<IReadOnlyList<McpServerStatus>> GetServersAsync(CancellationToken cancellationToken = default)
    {
        var statuses = _registry.GetServers().Select(e => new McpServerStatus(
            Name: e.Config.Name,
            Transport: e.Config.TransportDisplay,
            IsConnected: e.IsConnected,
            Error: e.Error,
            Tools: e.Tools.Select(t => new McpToolInfo(t.Name, t.Description ?? "")).ToList(),
            Config: e.Config
        )).ToList();
        return Task.FromResult<IReadOnlyList<McpServerStatus>>(statuses);
    }

    public async Task SaveServerAsync(McpServerConfig config, CancellationToken cancellationToken = default)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (string.IsNullOrWhiteSpace(config.Name)) throw new ArgumentException("name 必填", nameof(config));
        Directory.CreateDirectory(_directory);

        var path = Path.Combine(_directory, SafeFileName(config.Name) + ".json");
        var json = SerializeConfig(config);
        await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
        // 重连后把工具注册进 ToolRegistry（EnsureStartedAsync 内做 RegisterMcpTool）——
        // 仅 RefreshAsync 不注册工具，会导致「配置已写、重连成功，但工具没进可用池」的时灵时不灵。
        await _registry.RefreshAsync(cancellationToken).ConfigureAwait(false);
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteServerAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var path = Path.Combine(_directory, SafeFileName(name) + ".json");
        if (File.Exists(path)) File.Delete(path);
        await _registry.RefreshAsync(cancellationToken).ConfigureAwait(false);
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _registry.RefreshAsync(cancellationToken).ConfigureAwait(false);
        // 重连后把工具重新注册进 ToolRegistry（含清理后时灵时不灵，见 SaveServerAsync 注释）。
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StartHealthMonitoringAsync(TimeSpan interval, CancellationToken cancellationToken = default)
    {
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await _registry.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
                    await EnsureStartedAsync(cancellationToken).ConfigureAwait(false); // 重连后重新注册工具
                }
                catch { /* 监控失败不终止 */ }
            }
        }, cancellationToken);
        return Task.CompletedTask;
    }

    /// <summary>序列化为主流 mcpServers 映射格式（与 Claude Code .mcp.json 兼容）。</summary>
    private static string SerializeConfig(McpServerConfig c)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("mcpServers");
            writer.WriteStartObject(c.Name);
            if (!string.IsNullOrEmpty(c.Command))
            {
                writer.WriteString("command", c.Command);
                writer.WritePropertyName("args");
                writer.WriteStartArray();
                if (c.Arguments is not null) foreach (var a in c.Arguments) writer.WriteStringValue(a);
                writer.WriteEndArray();
                if (!string.IsNullOrEmpty(c.WorkingDirectory)) writer.WriteString("cwd", c.WorkingDirectory);
                if (c.Env is not null && c.Env.Count > 0)
                {
                    writer.WriteStartObject("env");
                    foreach (var kv in c.Env) writer.WriteString(kv.Key, kv.Value);
                    writer.WriteEndObject();
                }
            }
            else if (!string.IsNullOrEmpty(c.Url))
            {
                writer.WriteString("url", c.Url);
                if (c.Headers is not null && c.Headers.Count > 0)
                {
                    writer.WriteStartObject("headers");
                    foreach (var kv in c.Headers) writer.WriteString(kv.Key, kv.Value);
                    writer.WriteEndObject();
                }
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string SafeFileName(string name)
    {
        // 名字里只保留字母数字下划线中划线，避免路径越界。
        var sb = new System.Text.StringBuilder();
        foreach (var ch in name)
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_') sb.Append(ch);
        return sb.Length == 0 ? "server" : sb.ToString();
    }
}
