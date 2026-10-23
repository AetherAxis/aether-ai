using Aether.AI.Brains.Abstractions;
using Aether.AI.Core.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Aether.AI.Brains.Code;

public sealed class CodeBrainFactory : IContentBrainFactory
{
    private readonly ICodeBrainRegistry _registry;
    private readonly ILoggerFactory? _loggerFactory;

    public CodeBrainFactory(ICodeBrainRegistry registry, ILoggerFactory? loggerFactory = null)
    {
        _registry = registry;
        _loggerFactory = loggerFactory;
    }

    public EngineKind Kind => EngineKind.Code;
    public BrainCapabilities Capabilities => BrainCapabilities.Tools | BrainCapabilities.StatefulRun;

    public bool CanCreate(AgentDefinition definition) => definition.Engine == EngineKind.Code;

    public IBrain Create(AgentDefinition definition)
        => new RunScopedBrainRouter(() => CreateBrain(definition.Id),
            _loggerFactory?.CreateLogger<RunScopedBrainRouter>());

    public IChatClient CreateChatClient(AgentDefinition definition)
        => new BrainChatClient(Create(definition), _loggerFactory?.CreateLogger<BrainChatClient>());

    private IBrain CreateBrain(string id)
    {
        var created = _registry.TryCreate(id, out var brain, out var error);
        return created ? brain! : new MissingCodeBrain(error);
    }

    private sealed class MissingCodeBrain : IBrain
    {
        private readonly string _message;

        public MissingCodeBrain(string? error)
            => _message = string.IsNullOrWhiteSpace(error) ? "代码大脑未注册或已失效。" : $"代码大脑编译失败：{error}";

        public ValueTask<IReadOnlyList<Microsoft.Extensions.AI.AIContent>> DecideAsync(
            BrainContext context, CancellationToken cancellationToken)
            => new(new Microsoft.Extensions.AI.AIContent[] { new Microsoft.Extensions.AI.TextContent(_message) });
    }
}

public static class CodeBrainServiceCollectionExtensions
{
    public static IServiceCollection AddCodeBrains(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ICodeBrainRegistry, CodeBrainRegistry>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IBrainFactory, CodeBrainFactory>());
        return services;
    }
}
