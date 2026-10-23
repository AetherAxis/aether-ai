using Aether.AI.Brains.Code;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aether.AI.Brains.Code.Compilation;

public static class CodeCompilationServiceCollectionExtensions
{
    public static IServiceCollection AddCodeCompilation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddCodeBrains();
        services.TryAddSingleton<CodeCompilationService>();
        services.TryAddSingleton<ICodeCompilationService>(
            static provider => provider.GetRequiredService<CodeCompilationService>());
        return services;
    }
}
