using Aether.AI.Abstractions;

namespace Aether.AI.Mcp.Tests;

/// <summary>
/// <see cref="McpServerRegistry"/> 的连接编排（不启真实 server：用「既无 command 也无 url」的非法配置
/// 走失败分支）。重点不是连上，而是<b>连不上时的行为</b>：单个 server 失败不能拖垮其它、
/// 错误要带完整消息链、重连要重读配置。
/// </summary>
public class McpServerRegistryTests
{
    [Fact]
    public async Task Starting_without_configs_is_safe_and_empty()
    {
        await using var registry = new McpServerRegistry(() => []);

        await registry.StartAsync();

        Assert.True(registry.IsStarted);
        Assert.Empty(registry.GetServers());
        Assert.Empty(registry.GetTools());
    }

    [Fact]
    public async Task A_failing_server_records_its_error_without_throwing()
    {
        var configs = new[] { Invalid("broken-a"), Invalid("broken-b") };
        await using var registry = new McpServerRegistry(() => configs);

        await registry.StartAsync(); // 不应抛出

        var servers = registry.GetServers();
        Assert.Equal(2, servers.Count);
        Assert.All(servers, s =>
        {
            Assert.False(s.IsConnected);
            Assert.Contains("既未配置 Command", s.Error);
        });
        Assert.Empty(registry.GetTools());
    }

    [Fact]
    public async Task Refresh_rereads_the_config_provider()
    {
        var configs = new List<McpServerConfig> { Invalid("one") };
        await using var registry = new McpServerRegistry(() => configs);
        await registry.StartAsync();
        Assert.Single(registry.GetServers());

        configs.Add(Invalid("two"));
        await registry.RefreshAsync();

        Assert.Equal(2, registry.GetServers().Count);
    }

    [Fact]
    public async Task Start_is_shared_so_concurrent_callers_see_the_same_connection_round()
    {
        var reads = 0;
        await using var registry = new McpServerRegistry(() =>
        {
            Interlocked.Increment(ref reads);
            return [Invalid("only")];
        });

        await Task.WhenAll(registry.StartAsync(), registry.StartAsync(), registry.StartAsync());

        Assert.Equal(1, reads); // 配置只读一次：首跑发起，其余等同一次任务
        Assert.True(registry.IsStarted);
    }

    [Fact]
    public async Task Health_check_skips_entries_that_already_failed()
    {
        await using var registry = new McpServerRegistry(() => [Invalid("offline")]);
        await registry.StartAsync();

        await registry.CheckHealthAsync(); // 失败条目直接跳过，不触发重连（重连会重读配置）

        Assert.True(registry.IsStarted);
        Assert.Single(registry.GetServers());
    }

    [Fact]
    public async Task Dispose_is_idempotent()
    {
        var registry = new McpServerRegistry(() => [Invalid("x")]);
        await registry.StartAsync();

        await registry.DisposeAsync();
        await registry.DisposeAsync();

        Assert.Empty(registry.GetServers());
    }

    private static McpServerConfig Invalid(string name) => new() { Name = name };
}
