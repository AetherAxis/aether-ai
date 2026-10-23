using Aether.AI.Abstractions;

namespace Aether.AI.Mcp.Tests;

/// <summary>验证 <see cref="McpServerConfig"/> 的 TryParse/ToJson（编辑表单用）。</summary>
public class McpServerConfigTests
{
    [Fact]
    public void TryParse_mcpServers_map_uses_key_as_name()
    {
        // Trae / Claude Code 格式：key 即 name，内层无 name 字段。
        var json = """
        {
          "mcpServers": {
            "example-server": {
              "command": "dotnet",
              "args": ["run", "--project", "/p/SampleServer"],
              "env": {}
            }
          }
        }
        """;

        var config = McpServerConfig.TryParse(json);

        Assert.NotNull(config);
        Assert.Equal("example-server", config!.Name);
        Assert.Equal("dotnet", config.Command);
        Assert.Equal(new[] { "run", "--project", "/p/SampleServer" }, config.Arguments);
    }

    [Fact]
    public void TryParse_single_object_reads_name_field()
    {
        var config = McpServerConfig.TryParse("""{"name":"fs","command":"npx","args":["-y","x"]}""");

        Assert.NotNull(config);
        Assert.Equal("fs", config!.Name);
        Assert.Equal("npx", config.Command);
    }

    [Fact]
    public void TryParse_http_server()
    {
        var config = McpServerConfig.TryParse("""{"mcpServers":{"remote":{"url":"https://h/sse","headers":{"Authorization":"Bearer x"}}}}""");

        Assert.NotNull(config);
        Assert.Equal("remote", config!.Name);
        Assert.Equal("https://h/sse", config.Url);
        Assert.NotNull(config.Headers);
        Assert.Equal("Bearer x", config.Headers!["Authorization"]);
    }

    [Fact]
    public void TryParse_invalid_returns_null()
    {
        Assert.Null(McpServerConfig.TryParse("not json"));
        Assert.Null(McpServerConfig.TryParse(""));
        Assert.Null(McpServerConfig.TryParse("""{"mcpServers":{}}"""));
    }

    [Fact]
    public void ToJson_round_trips_through_TryParse_as_mcpServers_map()
    {
        var original = new McpServerConfig { Name = "s", Command = "c", Arguments = new[] { "a" } };

        var json = original.ToJson();
        var reparsed = McpServerConfig.TryParse(json);

        Assert.NotNull(reparsed);
        Assert.Equal("s", reparsed!.Name);
        Assert.Equal("c", reparsed.Command);
        Assert.Contains("mcpServers", json); // 输出为 mcpServers 映射格式
    }
}
