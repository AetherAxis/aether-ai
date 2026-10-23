using Aether.AI.Brains.Code.Compilation;
using Aether.AI.Core.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aether.AI.Brains.Code.Compilation.CSharp;

public sealed class CSharpCodeBrainCompiler : ICodeBrainCompiler
{
    public CodeBrainLanguage Language => CodeBrainLanguage.CSharp;

    public bool TryCompile(string source, out CompiledCodeBrain? compilation, out string? error)
    {
        if (!RuleScriptCompiler.TryCompile(source, out var instance, out error) || instance is null)
        {
            compilation = null;
            return false;
        }

        var type = instance.GetType();
        compilation = new CompiledCodeBrain(
            CodeCompilationService.SourceKey(Language, source),
            () => (Aether.AI.Brains.Abstractions.IBrain)Activator.CreateInstance(type)!);
        return true;
    }

    public void UnloadStale(IReadOnlySet<string> activeSourceKeys)
    {
        var hashes = activeSourceKeys
            .Select(key => key[(key.IndexOf(':') + 1)..])
            .ToHashSet(StringComparer.Ordinal);
        RuleScriptCompiler.UnloadStale(hashes);
    }
}

public static class CSharpCompilationServiceCollectionExtensions
{
    public static IServiceCollection AddCSharpBrainCompilation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddCodeCompilation();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICodeBrainCompiler, CSharpCodeBrainCompiler>());
        return services;
    }
}
