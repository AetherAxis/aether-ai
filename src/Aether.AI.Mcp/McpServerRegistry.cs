using System.Diagnostics;
using Aether.AI.Abstractions;
using Aether.AI.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Aether.AI.Mcp;

/// <summary>
/// 管理所有已配置 MCP server 的连接与工具。宿主启动时调用 <see cref="StartAsync"/> 连接各 server 并预解析工具；
/// <see cref="GetTools"/> 供 ToolRegistry 注册（专家选择器勾选）；<see cref="GetServers"/> 供管理 UI 展示状态+工具。
/// <para>配置经 <see cref="_configProvider"/> 获取——<see cref="RefreshAsync"/> 重读目录，支持增删改后重连。</para>
/// <para>单个 server 连接失败记 Error 并跳过，不阻断其它 server。</para>
/// </summary>
public sealed class McpServerRegistry : IMcpServerRegistry, IAsyncDisposable
{
    private readonly Func<IEnumerable<McpServerConfig>> _configProvider;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly List<Entry> _entries = new();
    private Task? _startTask;
    private readonly object _startLock = new();
    // RefreshAsync 串行化锁：健康监控（CheckHealthAsync → RefreshAsync）与手动 SaveServer/DeleteServer（→ RefreshAsync）
    // 可能并发触发「DisposeEntriesAsync + 重连」，dispose 正在被流使用的 McpClient 会崩（含 native 管道竞争）。
    // 用 SemaphoreSlim(1,1) 串行化整个 refresh 过程，避免并发 dispose/重建 _entries。
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    // _entries 的读写快照锁（Monitor）：GetServers/GetTools 读快照时与 RefreshAsync 的 Clear/Add 互斥。
    private readonly object _entriesLock = new();

    public McpServerRegistry(Func<IEnumerable<McpServerConfig>> configProvider, ILoggerFactory? loggerFactory = null)
    {
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _loggerFactory = loggerFactory;
    }

    public bool IsStarted => _startTask?.IsCompleted == true;

    public IReadOnlyList<AIFunction> GetTools()
    {
        lock (_entriesLock) return _entries.SelectMany(e => e.Tools).ToList();
    }

    public IReadOnlyList<McpServerEntry> GetServers()
        => GetServerSnapshot();

    private IReadOnlyList<McpServerEntry> GetServerSnapshot()
    {
        lock (_entriesLock)
        {
            return _entries.Select(e => new McpServerEntry
            {
                Config = e.Config,
                IsConnected = e.IsConnected,
                Tools = e.Tools,
                Error = e.Error,
            }).ToList();
        }
    }

    /// <summary>
    /// 连接所有已配置 server 并预解析工具。用 shared-task：首次调用发起连接，后续调用等同一个任务
    /// （保证 <c>await StartAsync()</c> 总能等到连接真正完成，供编辑器打开时可靠加载）。
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_startTask is not null) return _startTask;
        lock (_startLock)
        {
            if (_startTask is not null) return _startTask;
            _startTask = ConnectAllAsync(CancellationToken.None);
            return _startTask;
        }
    }

    /// <summary>重读目录并重连所有 server（关闭旧 client）。</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisposeEntriesAsync().ConfigureAwait(false);
            _startTask = null; // 失效，下次 StartAsync 重连
            await StartAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task ConnectAllAsync(CancellationToken cancellationToken)
    {
        var log = _loggerFactory?.CreateLogger<McpServerRegistry>();
        lock (_entriesLock) _entries.Clear();
        foreach (var cfg in _configProvider())
        {
            var entry = new Entry(cfg);
            lock (_entriesLock) _entries.Add(entry);
            try
            {
                entry.Client = await ConnectAsync(cfg, cancellationToken).ConfigureAwait(false);
                var tools = await entry.Client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                lock (_entriesLock) foreach (var t in tools)
                    if (t is AIFunction fn) entry.Tools.Add(fn);
                log?.LogInformation("已连接 MCP server {Name}，工具 {Count} 个。", cfg.Name, tools.Count);
            }
            catch (Exception ex)
            {
                // 透传完整异常消息链（含 inner），便于定位「command not found / 编译失败 / 握手超时」等真实原因。
                var sb = new System.Text.StringBuilder();
                for (var e = ex; e is not null; e = e.InnerException)
                {
                    if (sb.Length > 0) sb.Append(" → ");
                    sb.Append(e.Message);
                }
                entry.Error = sb.ToString();
                log?.LogWarning(ex, "连接 MCP server {Name} 失败，跳过。", cfg.Name);
            }
        }
    }

    private static async Task<McpClient> ConnectAsync(McpServerConfig cfg, CancellationToken ct)
    {
        if (cfg.IsStdio)
        {
            // CWD：未配置时用用户主目录，避免从 .app 启动时 CWD=/（只读）导致子进程异常。
            var workingDirectory = !string.IsNullOrEmpty(cfg.WorkingDirectory)
                ? cfg.WorkingDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            string command;
            List<string> arguments;
            if (Path.IsPathRooted(cfg.Command))
            {
                // 绝对路径（编译好的二进制）直接跑。
                command = cfg.Command!;
                arguments = cfg.Arguments?.ToList() ?? new List<string>();
            }
            else
            {
                // GUI 启动的 app 继承受限 PATH（缺 /usr/local/bin、/opt/homebrew/bin 等），Process.Start("dotnet")
                // 用父进程 PATH 解析命令——子进程 env 的 PATH 改了也来不及。故相对命令（dotnet/npx/python3）
                // 经登录 shell 跑（shell source profile 拿到完整 PATH 再 exec），像 Trae/VS Code 那样。
                var shell = Environment.GetEnvironmentVariable("SHELL");
                if (!string.IsNullOrEmpty(shell))
                {
                    command = shell;
                    var cmd = ShellQuote(cfg.Command!) + " " +
                              string.Join(" ", (cfg.Arguments ?? Array.Empty<string>()).Select(ShellQuote));
                    arguments = new List<string> { "-l", "-c", cmd };
                }
                else
                {
                    // Windows / 无 SHELL：相对命令直接跑（系统 PATH 通常已含 dotnet）。
                    command = cfg.Command!;
                    arguments = cfg.Arguments?.ToList() ?? new List<string>();
                }
            }

            var opts = new StdioClientTransportOptions
            {
                Command = command,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
            };
            if (cfg.Env is not null)
            {
                foreach (var kv in cfg.Env) opts.EnvironmentVariables[kv.Key] = kv.Value;
            }
            return await McpClient.CreateAsync(new StdioClientTransport(opts), cancellationToken: ct).ConfigureAwait(false);
        }

        if (cfg.IsSse)
        {
            // 1.2.0：SSE/流式 HTTP 统一走 HttpClientTransport，按 endpoint 自动协商传输模式。
            var opts = new HttpClientTransportOptions { Endpoint = new Uri(cfg.Url!) };
            if (cfg.Headers is not null)
            {
                foreach (var kv in cfg.Headers) opts.AdditionalHeaders.Add(kv.Key, kv.Value);
            }
            return await McpClient.CreateAsync(new HttpClientTransport(opts), cancellationToken: ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"MCP server '{cfg.Name}' 既未配置 Command(stdio) 也未配置 Url(http)。");
    }

    /// <summary>POSIX 单引号转义：把参数包成 '…'，内部 ' 转义为 '\''。</summary>
    private static string ShellQuote(string s)
    {
        if (string.IsNullOrEmpty(s)) return "''";
        return "'" + s.Replace("'", "'\\''") + "'";
    }

    /// <summary>健康检查：探活每个已连接 server（ListToolsAsync），任何掉线则 RefreshAsync 重连。</summary>
    public async Task CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        // 快照 entries，避免探活过程中 RefreshAsync 并发 Clear/重建导致「Collection was modified」。
        List<Entry> snapshot;
        lock (_entriesLock) { snapshot = _entries.ToList(); }

        foreach (var entry in snapshot)
        {
            if (entry.Client is null || entry.Error is not null) continue;
            try
            {
                await entry.Client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // server 掉线 → 重连全部
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
        }
    }

    public async ValueTask DisposeAsync() => await DisposeEntriesAsync().ConfigureAwait(false);

    private async Task DisposeEntriesAsync()
    {
        // 先快照（持锁），再逐个 DisposeAsync（await 在锁外，避免锁跨 await）。
        List<Entry> snapshot;
        lock (_entriesLock) { snapshot = _entries.ToList(); _entries.Clear(); }

        foreach (var e in snapshot)
        {
            if (e.Client is not null)
            {
                try
                {
                    // 每个 client 释放加 2s 超时：MAF 的 McpClient.DisposeAsync 关闭 stdio 子进程
                    // （等子进程退出）无超时，子进程不响应会死等 → 退出卡死。超时后放弃，残留子进程由 OS 回收。
                    await e.Client.DisposeAsync()
                        .AsTask()
                        .WaitAsync(TimeSpan.FromSeconds(2))
                        .ConfigureAwait(false);
                }
                catch { }
            }
        }
    }

    private sealed class Entry
    {
        public McpServerConfig Config { get; }
        public McpClient? Client { get; set; }
        public List<AIFunction> Tools { get; } = new();
        public string? Error { get; set; }
        public bool IsConnected => Client is not null && Error is null;
        public Entry(McpServerConfig config) => Config = config;
    }
}
