using Aether.AI.Brains.Code.Compilation;

namespace Aether.AI.Harness.Scripting;

/// <summary>Compatibility adapter over the language-neutral compilation service.</summary>
public sealed class RuleScriptingService : IRuleScriptingService
{
    private readonly ICodeCompilationService _inner;

    public RuleScriptingService(ICodeCompilationService inner)
    {
        _inner = inner;
        _inner.Loaded += () => RulesLoaded?.Invoke();
    }

    public IReadOnlyDictionary<string, string> LoadErrors => _inner.LoadErrors;
    public event Action? RulesLoaded;
    public Task LoadAsync(CancellationToken cancellationToken = default) => _inner.LoadAsync(cancellationToken);
    public void Reload() => _inner.Reload();
}
