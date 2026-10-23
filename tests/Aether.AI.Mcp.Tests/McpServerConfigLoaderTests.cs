using Aether.AI.Mcp;

namespace Aether.AI.Mcp.Tests;

/// <summary>
/// 验证 <see cref="McpServerConfigLoader"/> 兼容主流 <c>.mcp.json</c>（Claude Code / Cursor）格式，
/// 以及单对象 / 数组格式。
/// </summary>
public class McpServerConfigLoaderTests : IDisposable
{
    private readonly string _dir;

    public McpServerConfigLoaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"mcp-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void Parses_mainstream_mcpServers_map()
    {
        File.WriteAllText(Path.Combine(_dir, "servers.json"), """
        {
          "mcpServers": {
            "fs": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"], "env": { "API_KEY": "k" } },
            "remote": { "url": "https://host/sse", "headers": { "Authorization": "Bearer x" } }
          }
        }
        """);

        var configs = McpServerConfigLoader.LoadFromDirectory(_dir);

        Assert.Equal(2, configs.Count);
        var fs = configs.Single(c => c.Name == "fs");
        Assert.Equal("npx", fs.Command);
        Assert.Equal(new[] { "-y", "@modelcontextprotocol/server-filesystem", "/tmp" }, fs.Arguments);
        Assert.NotNull(fs.Env);
        Assert.Equal("k", fs.Env!["API_KEY"]);
        Assert.True(fs.IsStdio);

        var remote = configs.Single(c => c.Name == "remote");
        Assert.Equal("https://host/sse", remote.Url);
        Assert.NotNull(remote.Headers);
        Assert.Equal("Bearer x", remote.Headers!["Authorization"]);
        Assert.True(remote.IsSse);
    }

    [Fact]
    public void Parses_single_object_with_arguments_alias()
    {
        File.WriteAllText(Path.Combine(_dir, "one.json"), """
        { "name": "sample", "command": "dotnet", "arguments": ["run"] }
        """);

        var configs = McpServerConfigLoader.LoadFromDirectory(_dir);

        var c = Assert.Single(configs);
        Assert.Equal("sample", c.Name);
        Assert.Equal(new[] { "run" }, c.Arguments);
    }

    [Fact]
    public void Parses_array()
    {
        File.WriteAllText(Path.Combine(_dir, "arr.json"), """
        [ { "name": "a", "command": "x" }, { "name": "b", "url": "https://h" } ]
        """);

        var configs = McpServerConfigLoader.LoadFromDirectory(_dir);

        Assert.Equal(2, configs.Count);
        Assert.Contains(configs, c => c.Name == "a" && c.Command == "x");
        Assert.Contains(configs, c => c.Name == "b" && c.Url == "https://h");
    }

    [Fact]
    public void Malformed_file_is_skipped()
    {
        File.WriteAllText(Path.Combine(_dir, "bad.json"), "{ not valid json");
        File.WriteAllText(Path.Combine(_dir, "good.json"), """{ "mcpServers": { "ok": { "command": "c" } } }""");

        var configs = McpServerConfigLoader.LoadFromDirectory(_dir);

        var c = Assert.Single(configs);
        Assert.Equal("ok", c.Name);
    }

    [Fact]
    public void Missing_directory_returns_empty()
    {
        Assert.Empty(McpServerConfigLoader.LoadFromDirectory(Path.Combine(_dir, "nope")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
