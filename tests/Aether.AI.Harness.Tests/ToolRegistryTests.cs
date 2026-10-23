using System.ComponentModel;
using Aether.AI.Core.Agents;
using Aether.AI.Harness.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.AI.Harness.Tests;

public class ToolRegistryTests
{
    private static readonly IServiceProvider EmptyProvider = new ServiceCollection().BuildServiceProvider();

    [Fact]
    public void Register_and_Resolve_by_name()
    {
        var registry = new ToolRegistry();

        registry.Register("GetWeather", "Get the weather for a city.", "test",
            _ => AIFunctionFactory.Create((Func<string, string>)GetWeather));

        Assert.True(registry.Contains("GetWeather"));
        var resolved = registry.ResolveAll(["GetWeather"]);
        Assert.Single(resolved);
        Assert.Equal("GetWeather", resolved[0].Name);
    }

    [Fact]
    public void ResolveAll_skips_tools_unavailable_on_current_host()
    {
        // 桌面宿主：DesktopOnly 工具可用，MobileOnly 工具被跳过；
        // 手机宿主：反之。All 工具两端都可用。
        var desktop = new ToolRegistry(EmptyProvider, () => HostCapability.DesktopOnly);
        desktop.Register("Desk", "desktop-only", "test",
            _ => AIFunctionFactory.Create((Func<string, string>)GetWeather), hostCapability: HostCapability.DesktopOnly);
        desktop.Register("Mob", "mobile-only", "test",
            _ => AIFunctionFactory.Create((Func<string, string>)GetWeather), hostCapability: HostCapability.MobileOnly);
        desktop.Register("Both", "both", "test",
            _ => AIFunctionFactory.Create((Func<string, string>)GetWeather));

        var desktopResolved = desktop.ResolveAll(["Desk", "Mob", "Both"]);
        Assert.Equal(2, desktopResolved.Count);
        Assert.DoesNotContain(desktopResolved, f => f.Name == "Mob");

        var mobile = new ToolRegistry(EmptyProvider, () => HostCapability.MobileOnly);
        mobile.Register("Desk", "desktop-only", "test",
            _ => AIFunctionFactory.Create((Func<string, string>)GetWeather), hostCapability: HostCapability.DesktopOnly);
        mobile.Register("Mob", "mobile-only", "test",
            _ => AIFunctionFactory.Create((Func<string, string>)GetWeather), hostCapability: HostCapability.MobileOnly);

        var mobileResolved = mobile.ResolveAll(["Desk", "Mob"]);
        // 手机宿主下 DesktopOnly 的 Desk 被跳过，仅 MobileOnly 的 Mob 存活。
        Assert.Single(mobileResolved);
    }

    [Fact]
    public void GetAllMetas_exposes_host_capability()
    {
        var registry = new ToolRegistry();
        registry.Register("GetWeather", "Get the weather for a city.", "test",
            _ => AIFunctionFactory.Create((Func<string, string>)GetWeather),
            hostCapability: HostCapability.DesktopOnly);

        var metas = registry.GetAllMetas();
        Assert.Single(metas);
        Assert.Equal(HostCapability.DesktopOnly, metas[0].HostCapability);
    }

    [Fact]
    public void Default_host_is_all_no_filtering()
    {
        // 未注入 currentHost（库测试/不声明宿主）→ 视为 All，不做任何过滤。
        var registry = new ToolRegistry();
        registry.Register("Desk", "desktop-only", "test",
            _ => AIFunctionFactory.Create((Func<string, string>)GetWeather), hostCapability: HostCapability.DesktopOnly);

        var resolved = registry.ResolveAll(["Desk"]);
        Assert.Single(resolved);
    }

    [Fact]
    public void ResolveAll_skips_missing_names()
    {
        var registry = new ToolRegistry();
        registry.Register("GetWeather", "Get the weather for a city.", "test",
            _ => AIFunctionFactory.Create((Func<string, string>)GetWeather));

        var resolved = registry.ResolveAll(["GetWeather", "DoesNotExist"]);
        Assert.Single(resolved);
        Assert.Equal("GetWeather", resolved[0].Name);
    }

    [Fact]
    public void GetAllMetas_returns_category_and_description()
    {
        var registry = new ToolRegistry();
        registry.Register("GetWeather", "Get the weather for a city.", "test",
            _ => AIFunctionFactory.Create((Func<string, string>)GetWeather));

        var metas = registry.GetAllMetas();

        Assert.Single(metas);
        Assert.Equal("GetWeather", metas[0].Name);
        Assert.Equal("test", metas[0].Category);
        Assert.Equal("Get the weather for a city.", metas[0].Description);
    }

    [Description("Get the weather for a city.")]
    static string GetWeather([Description("The city.")] string city) => $"{city}: sunny";
}
