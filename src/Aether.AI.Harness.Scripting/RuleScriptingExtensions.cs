using Aether.AI.Brains.Code;
using Aether.AI.Brains.Code.Compilation;
using Aether.AI.Brains.Code.Compilation.CSharp;
using Aether.AI.Brains.Code.Compilation.FSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aether.AI.Harness.Scripting;

/// <summary>Compatibility composition that installs both language compilers.</summary>
public static class RuleScriptingExtensions
{
    public static IServiceCollection AddRuleScripting(this IServiceCollection services)
    {
        services.AddCodeBrains();
        services.AddCSharpBrainCompilation();
        services.AddFSharpBrainCompilation();
        services.TryAddSingleton<Aether.AI.Brains.Code.Compilation.ICodeNoteSource>(sp =>
            new CompatibilityCodeNoteSource(sp.GetRequiredService<ICodeNoteSource>()));
        services.TryAddSingleton<CodeCompilationService>();
        services.TryAddSingleton<ICodeCompilationService>(sp => sp.GetRequiredService<CodeCompilationService>());
        services.TryAddSingleton<IRuleScriptingService, RuleScriptingService>();
        return services;
    }

    private sealed class CompatibilityCodeNoteSource : Aether.AI.Brains.Code.Compilation.ICodeNoteSource
    {
        private readonly ICodeNoteSource _inner;

        public CompatibilityCodeNoteSource(ICodeNoteSource inner)
        {
            _inner = inner;
        }

        public async Task<IReadOnlyList<Aether.AI.Brains.Code.Compilation.CodeNote>> GetAllAsync(
            CancellationToken cancellationToken = default)
        {
            var notes = await _inner.GetAllAsync(cancellationToken).ConfigureAwait(false);
            return notes.Select(note => new Aether.AI.Brains.Code.Compilation.CodeNote(
                note.Id, note.Title, note.Content, note.Language)).ToArray();
        }
    }
}
