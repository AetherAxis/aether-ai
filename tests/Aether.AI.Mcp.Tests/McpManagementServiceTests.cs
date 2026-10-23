using Aether.AI.Abstractions;
using Aether.AI.Core.Agents;
using Aether.AI.Harness.Tools;
using Microsoft.Extensions.AI;

namespace Aether.AI.Mcp.Tests;

/// <summary>
/// MCP 管理服务的行为钉子（用假注册表，不连真 server）：写盘格式必须与主流 <c>.mcp.json</c> 互通，
/// 增删改后必须重连 + 重新注册工具，且「工具池」里旧 MCP 工具要被清掉——否则删除的 server 会留下
/// 指向已 dispose client 的僵尸工具。
/// </summary>
public class McpManagementServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "aether-mcp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public async Task Save_writes_a_mainstream_mcpServers_file_and_reconnects()
    {
        var registry = new FakeRegistry();
        var service = NewService(registry);
        var changed = 0;
        service.Changed += (_, _) => changed++;

        await service.SaveServerAsync(new McpServerConfig
        {
            Name = "my server/1",
            Command = "dotnet",
            Arguments = ["run", "--project", "/p/Sample"],
            WorkingDirectory = "/tmp",
            Env = new Dictionary<string, string> { ["KEY"] = "value" },
        });

        // 文件名只留字母数字（防路径越界），内容仍是主流 mcpServers 映射格式。
        var path = Path.Combine(_directory, "myserver1.json");
        Assert.True(File.Exists(path));
        var json = await File.ReadAllTextAsync(path);
        Assert.Contains("\"mcpServers\"", json);
        Assert.Contains("\"my server/1\"", json);
        Assert.Contains("\"cwd\": \"/tmp\"", json);

        // 写盘后必须重连（Refresh + Start）并广播变更。
        Assert.Equal(1, registry.RefreshCalls);
        Assert.True(registry.StartCalls >= 1);
        Assert.Equal(1, changed);

        // 与加载器闭环：写出去的能被原样读回来。
        var reloaded = Assert.Single(McpServerConfigLoader.LoadFromDirectory(_directory));
        Assert.Equal("my server/1", reloaded.Name);
        Assert.Equal("dotnet", reloaded.Command);
        Assert.Equal(new[] { "run", "--project", "/p/Sample" }, reloaded.Arguments);
        Assert.Equal("value", reloaded.Env!["KEY"]);
    }

    [Fact]
    public async Task Save_rejects_missing_config_or_name()
    {
        var service = NewService(new FakeRegistry());

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.SaveServerAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.SaveServerAsync(new McpServerConfig { Name = "  ", Command = "dotnet" }));
    }

    [Fact]
    public async Task Delete_removes_the_file_even_for_unknown_names()
    {
        var registry = new FakeRegistry();
        var service = NewService(registry);
        var changed = 0;
        service.Changed += (_, _) => changed++;

        await service.SaveServerAsync(new McpServerConfig { Name = "temp", Command = "dotnet" });
        Assert.True(File.Exists(Path.Combine(_directory, "temp.json")));

        await service.DeleteServerAsync("temp");
        Assert.False(File.Exists(Path.Combine(_directory, "temp.json")));

        // 删不存在的名字：不抛，仍刷新并广播（UI 保持一致性）。
        await service.DeleteServerAsync("never-existed");
        Assert.Equal(3, registry.RefreshCalls); // save + 两次 delete 各刷新一次
        Assert.Equal(3, changed);
    }

    [Fact]
    public async Task EnsureStarted_registers_tools_and_syncs_stale_mcp_entries()
    {
        var toolRegistry = new ToolRegistry();
        toolRegistry.Register("Native", "原生工具", "文件", _ => Func("Native"));
        toolRegistry.RegisterMcpTool(Func("mcp__stale"));
        var registry = new FakeRegistry();
        registry.Tools.Add(Func("mcp__fresh"));
        var service = NewService(registry, toolRegistry);

        await service.EnsureStartedAsync();

        Assert.True(toolRegistry.Contains("mcp__fresh"));
        Assert.False(toolRegistry.Contains("mcp__stale")); // 上次连接残留 → 清掉
        Assert.True(toolRegistry.Contains("Native"));      // 原生工具不动
        Assert.Equal(ToolApprovalLevel.Dangerous, toolRegistry.LevelOf("mcp__fresh"));
    }

    [Fact]
    public async Task GetServers_maps_entries_to_ui_status()
    {
        var registry = new FakeRegistry();
        registry.Servers.Add(new McpServerEntry
        {
            Config = new McpServerConfig { Name = "s1", Command = "dotnet" },
            IsConnected = true,
            Tools = [Func("mcp__one")],
        });
        registry.Servers.Add(new McpServerEntry
        {
            Config = new McpServerConfig { Name = "s2", Url = "https://example.test/mcp" },
            Error = "连接失败",
        });
        var service = NewService(registry);

        var statuses = await service.GetServersAsync();

        Assert.Equal(2, statuses.Count);
        Assert.Equal("stdio: dotnet", statuses[0].Transport);
        Assert.True(statuses[0].IsConnected);
        Assert.Equal("mcp__one", Assert.Single(statuses[0].Tools).Name);
        Assert.Equal("http: https://example.test/mcp", statuses[1].Transport);
        Assert.False(statuses[1].IsConnected);
        Assert.Equal("连接失败", statuses[1].Error);
    }

    private McpManagementService NewService(FakeRegistry registry, ToolRegistry? tools = null)
        => new(registry, new McpManagementServiceOptions(_directory), tools);

    private static AIFunction Func(string name)
        => AIFunctionFactory.Create((Func<string, string>)(text => text), name: name);

    private sealed class FakeRegistry : IMcpServerRegistry
    {
        public List<McpServerEntry> Servers { get; } = new();
        public List<AIFunction> Tools { get; } = new();
        public int StartCalls { get; private set; }
        public int RefreshCalls { get; private set; }
        public int HealthCalls { get; private set; }

        public bool IsStarted => StartCalls > 0;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCalls++;
            return Task.CompletedTask;
        }

        public Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            return Task.CompletedTask;
        }

        public Task CheckHealthAsync(CancellationToken cancellationToken = default)
        {
            HealthCalls++;
            return Task.CompletedTask;
        }

        public IReadOnlyList<AIFunction> GetTools() => Tools;

        public IReadOnlyList<McpServerEntry> GetServers() => Servers;
    }
}
