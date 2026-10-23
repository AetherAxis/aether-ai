using Aether.AI.Core.Agents;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Skills;
using Aether.AI.Harness.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.AI.Hosting;

/// <summary>
/// Builder returned by <see cref="AetherAIServiceCollectionExtensions.AddAetherAI"/>.
/// Used to register agents and tools against the Aether runtime.
/// </summary>
public sealed class AetherAIBuilder
{
    private readonly IServiceCollection _services;

    public AetherAIBuilder(IServiceCollection services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>The underlying service collection, exposed so feature-specific
    /// extensions (e.g. a business AI adapter) can register their own services.</summary>
    public IServiceCollection Services => _services;

    /// <summary>Configures <see cref="AetherAIOptions"/>.</summary>
    public AetherAIBuilder Configure(Action<AetherAIOptions> configure)
    {
        _services.Configure(configure);
        return this;
    }

    /// <summary>Registers an agent definition built with <see cref="AgentBuilder"/>.</summary>
    public AetherAIBuilder AddAgent(string id, Action<AgentBuilder> configure)
    {
        var builder = new AgentBuilder(id);
        configure(builder);
        var definition = builder.Build();
        _services.AddSingleton(definition);
        return this;
    }

    /// <summary>Registers an agent definition built with <see cref="AgentBuilder"/> (auto-generated id).</summary>
    public AetherAIBuilder AddAgent(Action<AgentBuilder> configure)
        => AddAgent(Guid.NewGuid().ToString("N"), configure);

    /// <summary>Registers an agent definition directly.</summary>
    public AetherAIBuilder AddAgent(AgentDefinition definition)
    {
        _services.AddSingleton(definition);
        return this;
    }

    /// <summary>Registers a pre-built <see cref="AIFunction"/> tool.</summary>
    public AetherAIBuilder AddTool(AIFunction tool)
    {
        _services.AddSingleton(tool);
        return this;
    }

    /// <summary>
    /// Creates a tool from a delegate and registers it. Annotate the method
    /// and its parameters with <c>[Description]</c> so the model can call it.
    /// </summary>
    public AetherAIBuilder AddTool(Delegate method, string? name = null)
    {
        _services.AddSingleton(AIFunctionFactory.Create(method, name));
        return this;
    }

    /// <summary>
    /// 配置 ToolRegistry（注册工具元数据 + 工厂）。
    /// 每个 AI 适配器在 AddXxxTools 中调用此方法注册自己的工具。
    /// </summary>
    public AetherAIBuilder ConfigureToolRegistry(Action<ToolRegistry> configure)
    {
        _services.AddSingleton<IToolRegistryContributor>(new DelegateToolRegistryContributor(configure));
        return this;
    }

    /// <summary>
    /// 配置 SkillRegistry（注册提示词级 skill：name/description/category + 指令正文）。
    /// 每个 AI 适配器在 AddXxxSkills 中调用此方法注册自己的 skill。
    /// </summary>
    public AetherAIBuilder ConfigureSkillRegistry(Action<SkillRegistry> configure)
    {
        _services.AddSingleton<ISkillRegistryContributor>(new DelegateSkillRegistryContributor(configure));
        return this;
    }
}
