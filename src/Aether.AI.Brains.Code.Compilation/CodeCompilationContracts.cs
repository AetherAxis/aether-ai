using Aether.AI.Brains.Abstractions;
using Aether.AI.Core.Agents;

namespace Aether.AI.Brains.Code.Compilation;

public sealed record CompiledCodeBrain(string SourceKey, Func<IBrain> Create);

public interface ICodeBrainCompiler
{
    CodeBrainLanguage Language { get; }
    bool TryCompile(string source, out CompiledCodeBrain? compilation, out string? error);
    void UnloadStale(IReadOnlySet<string> activeSourceKeys);
}

public sealed record CodeNote(string Id, string Title, string? Content, string? Language = null);

public interface ICodeNoteSource
{
    Task<IReadOnlyList<CodeNote>> GetAllAsync(CancellationToken cancellationToken = default);
}

public interface ICodeCompilationService
{
    IReadOnlyDictionary<string, string> LoadErrors { get; }
    event Action? Loaded;
    Task LoadAsync(CancellationToken cancellationToken = default);
    void Reload();
}
