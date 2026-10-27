using Aether.AI.Abstractions;
using Aether.AI.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Aether.AI.Mcp;

/// <summary>
/// <see cref="AetherAIBuilder"/> 的 MCP 扩展：注册 MCP server 配置 + <see cref="McpServerRegistry"/> +
/// <see cref="IMcpManagementService"/>（管理 UI 用）。
/// </summary>
public static class AetherAIBuilderMcpExtensions
{
    /// <summary>添加一个 MCP server（stdio 或 SSE，编程式）。可多次调用注册多个。</summary>
    public static AetherAIBuilder AddMcpServer(this AetherAIBuilder builder, McpServerConfig config)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        builder.Services.AddSingleton(config);
        EnsureRegistry(builder.Services, sp => sp.GetServices<McpServerConfig>());
        return builder;
    }

    /// <summary>
    /// 从目录扫描 MCP server 配置（<c>*.json</c>，兼容主流 <c>.mcp.json</c>）并注册。目录不存在则创建。
    /// 同时注册 <see cref="IMcpManagementService"/>（管理 UI 增删改 server，写回此目录）。镜像 skills 目录模式。
    /// </summary>
    public static AetherAIBuilder AddMcpServersFromDirectory(this AetherAIBuilder builder, string directory)
        => AddMcpServersFromDirectory(builder, directory, filter: null);

    /// <summary>
    /// 同 <see cref="AddMcpServersFromDirectory(AetherAIBuilder,string)"/>，但按宿主裁掉跑不了的 server：
    /// <paramref name="filter"/> 为 <c>null</c> 时全部保留。手机端传「只要 HTTP/SSE」——
    /// stdio（<c>command</c>+<c>args</c>）要 spawn 子进程，iOS 沙盒直接禁止；HTTP 连接器两端同源可用，
    /// 这正是「手机桌面一体」的取舍点（对齐豆包：连接器是远端服务，技能包才分本地/云端执行）。
    /// 被裁掉的 server 连**连接尝试**都不发生（不报错、不进管理列表），配置 JSON 原样留在盘上，
    /// 桌面端读到它就自动生效。
    /// </summary>
    public static AetherAIBuilder AddMcpServersFromDirectory(this AetherAIBuilder builder, string directory,
        Func<McpServerConfig, bool>? filter)
    {
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        EnsureRegistry(builder.Services, _ =>
        {
            var configs = McpServerConfigLoader.LoadFromDirectory(directory);
            return filter is null ? configs : configs.Where(filter).ToList();
        });
        builder.Services.AddSingleton(new McpManagementServiceOptions(directory));
        builder.Services.AddSingleton<IMcpManagementService, McpManagementService>();
        return builder;
    }

    private static void EnsureRegistry(IServiceCollection services, Func<IServiceProvider, IEnumerable<McpServerConfig>> provider)
    {
        if (services.Any(d => d.ServiceType == typeof(IMcpServerRegistry))) return;
        services.AddSingleton<McpServerRegistry>(sp => new McpServerRegistry(() => provider(sp), sp.GetService<ILoggerFactory>()));
        services.AddSingleton<IMcpServerRegistry>(sp => sp.GetRequiredService<McpServerRegistry>());
    }
}

/// <summary>管理服务选项：配置目录路径。</summary>
public sealed class McpManagementServiceOptions(string directory)
{
    public string Directory { get; } = directory;
}

/// <summary>
/// 从目录加载 <see cref="McpServerConfig"/>：扫描 <c>*.json</c>。兼容三种格式：
/// <list type="bullet">
/// <item><b>主流 mcpServers 映射</b>（Claude Code / Cursor 的 <c>.mcp.json</c>，可直接复用）：
///   <c>{ "mcpServers": { "name": { "command","args","env","url","headers" } } }</c></item>
/// <item><b>单个 server 对象</b>：<c>{ "name","command","args","env","url","headers" }</c></item>
/// <item><b>数组</b>：<c>[ { ... }, { ... } ]</c></item>
/// </list>
/// 字段：<c>command</c>+<c>args</c>+<c>env</c>（stdio）或 <c>url</c>+<c>headers</c>（HTTP）。
/// 也接受 <c>arguments</c> 作为 <c>args</c> 的别名。解析失败的文件跳过。
/// </summary>
public static class McpServerConfigLoader
{
    public static List<McpServerConfig> LoadFromDirectory(string directory)
    {
        var list = new List<McpServerConfig>();
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return list;

        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                using var doc = JsonDocument.Parse(json);
                AddFromElement(doc.RootElement, list);
            }
            catch
            {
                /* 解析失败的配置文件跳过，不阻断启动 */
            }
        }
        return list;
    }

    private static void AddFromElement(JsonElement el, List<McpServerConfig> list)
    {
        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object) list.Add(McpServerConfig.ParseElement(item, null));
        }
        else if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("mcpServers", out var map) && map.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in map.EnumerateObject())
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                        list.Add(McpServerConfig.ParseElement(prop.Value, prop.Name));
            }
            else
            {
                list.Add(McpServerConfig.ParseElement(el, null));
            }
        }
    }
}
