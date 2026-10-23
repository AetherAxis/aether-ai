using Aether.AI.Brains.Code.Compilation;
using Aether.AI.Core.Agents;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.AI.Brains.Code.Compilation.FSharp;

public sealed class FSharpCodeBrainCompiler : ICodeBrainCompiler
{
    public CodeBrainLanguage Language => CodeBrainLanguage.FSharp;

    public bool TryCompile(string source, out CompiledCodeBrain? compilation, out string? error)
    {
        if (!FsRuleScriptCompiler.TryCompile(source, out var instance, out error) || instance is null)
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
        => FsRuleScriptCompiler.UnloadStale(activeSourceKeys);
}

public static class FSharpCompilationServiceCollectionExtensions
{
    public static IServiceCollection AddFSharpBrainCompilation(this IServiceCollection services)
    {
        services.AddSingleton<ICodeBrainCompiler, FSharpCodeBrainCompiler>();
        return services;
    }
}
