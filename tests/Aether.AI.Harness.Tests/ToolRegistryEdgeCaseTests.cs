using Aether.AI.Core.Agents;
using Aether.AI.Harness.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.AI.Harness.Tests;

/// <summary>
/// <see cref="ToolRegistry"/> 的边界语义——正常路径见 <see cref="ToolRegistryTests"/>，这里钉住
/// 「重名 / MCP 工具生命周期 / 工厂炸了」这些只在运行时才会遇到的路径：
/// ① 原生工具重名 = 覆盖（适配器热重载配置）；② MCP 工具重名 = 保留先到者（不抢原生工具）；
/// ③ 重连后 <see cref="ToolRegistry.SyncMcpTools"/> 只清 MCP 类，不误删原生工具；
/// ④ 单个工具实例化失败不能拖垮整批解析。
/// </summary>
public class ToolRegistryEdgeCaseTests
{
    [Fact]
    public void Re_registering_overwrites_metadata_and_level()
    {
        var registry = new ToolRegistry();
        registry.Register("Read", "旧描述", "旧分类", _ => NewFunc("Read"), ToolApprovalLevel.Safe);
        registry.Register("Read", "新描述", "新分类", _ => NewFunc("Read"), ToolApprovalLevel.Confirm);

        var meta = Assert.Single(registry.GetAllMetas());
        Assert.Equal("新描述", meta.Description);
        Assert.Equal("新分类", meta.Category);
        Assert.Equal(ToolApprovalLevel.Confirm, registry.LevelOf("Read"));
        Assert.Single(registry.AllNames);
    }

    [Fact]
    public void Mcp_tools_do_not_overwrite_an_existing_registration_and_default_to_dangerous()
    {
        var registry = new ToolRegistry();
        registry.Register("Native", "原生工具", "文件", _ => NewFunc("Native"), ToolApprovalLevel.Safe);

        // 重名：TryAdd 失败，原生工具仍是 Safe。
        Assert.False(registry.RegisterMcpTool(NewFunc("Native"), category: "MCP"));
        Assert.Equal(ToolApprovalLevel.Safe, registry.LevelOf("Native"));

        // 新名字：登记成功，来源未知默认 Dangerous。
        Assert.True(registry.RegisterMcpTool(NewFunc("mcp__search"), category: "MCP"));
        Assert.Equal(ToolApprovalLevel.Dangerous, registry.LevelOf("mcp__search"));
    }

    [Fact]
    public void SyncMcpTools_removes_only_stale_mcp_entries()
    {
        var registry = new ToolRegistry();
        registry.Register("Native", "原生工具", "文件", _ => NewFunc("Native"));
        registry.RegisterMcpTool(NewFunc("mcp__keep"));
        registry.RegisterMcpTool(NewFunc("mcp__gone"));

        registry.SyncMcpTools(["mcp__keep"]);

        Assert.True(registry.Contains("mcp__keep"));
        Assert.False(registry.Contains("mcp__gone"));
        // 非 MCP 分类的条目即便不在「当前 MCP 工具」列表里也不能被清掉。
        Assert.True(registry.Contains("Native"));
    }

    [Fact]
    public void ResolveAll_skips_tools_whose_factory_throws()
    {
        var registry = new ToolRegistry();
        registry.Register("Good", "ok", "测试", _ => NewFunc("Good"));
        registry.Register("Broken", "MCP server 已断开", "MCP", _ => throw new InvalidOperationException("client disposed"));

        var resolved = registry.ResolveAll(["Broken", "Good", "Missing"]);

        var tool = Assert.Single(resolved);
        Assert.Equal("Good", tool.Name);
    }

    [Fact]
    public void AllNames_and_Contains_reflect_the_registry()
    {
        var registry = new ToolRegistry();
        registry.Register("A", "a", "测试", _ => NewFunc("A"));
        registry.Register("B", "b", "测试", _ => NewFunc("B"));

        Assert.Equal(new[] { "A", "B" }, registry.AllNames.OrderBy(n => n));
        Assert.True(registry.Contains("A"));
        Assert.False(registry.Contains("C"));
    }

    [Fact]
    public void Contributors_are_invoked_at_construction_and_a_throwing_one_is_skipped()
    {
        var services = new ServiceCollection()
            .AddSingleton<IToolRegistryContributor>(new DelegateToolRegistryContributor(
                r => r.Register("FromContributor", "贡献者注册", "测试", _ => NewFunc("FromContributor"))))
            .AddSingleton<IToolRegistryContributor>(new DelegateToolRegistryContributor(
                _ => throw new InvalidOperationException("bad adapter")))
            .BuildServiceProvider();

        var registry = new ToolRegistry(services);

        Assert.True(registry.Contains("FromContributor"));
    }

    private static AIFunction NewFunc(string name)
        => AIFunctionFactory.Create((Func<string, string>)(text => text), name: name);
}
