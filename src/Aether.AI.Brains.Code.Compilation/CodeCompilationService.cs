using System.Security.Cryptography;
using System.Text;
using Aether.AI.Brains.Code;
using Aether.AI.Core.Agents;
using Microsoft.Extensions.Logging;

namespace Aether.AI.Brains.Code.Compilation;

public sealed class CodeCompilationService : ICodeCompilationService
{
    private readonly ICodeNoteSource _source;
    private readonly ICodeBrainRegistry _registry;
    private readonly IReadOnlyDictionary<CodeBrainLanguage, ICodeBrainCompiler> _compilers;
    private readonly ILogger<CodeCompilationService> _logger;
    private readonly Dictionary<string, string> _loadErrors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _registeredIds = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private CancellationTokenSource? _debounce;
    private int _loading;

    public CodeCompilationService(
        ICodeNoteSource source,
        ICodeBrainRegistry registry,
        IEnumerable<ICodeBrainCompiler> compilers,
        ILogger<CodeCompilationService> logger)
    {
        _source = source;
        _registry = registry;
        _compilers = compilers.ToDictionary(c => c.Language);
        _logger = logger;
    }

    public IReadOnlyDictionary<string, string> LoadErrors
    {
        get { lock (_gate) return new Dictionary<string, string>(_loadErrors); }
    }

    public event Action? Loaded;

    public void Reload()
    {
        _debounce?.Cancel();
        _debounce = new CancellationTokenSource();
        var token = _debounce.Token;
        _ = Task.Delay(500, token).ContinueWith(
            _ => LoadAsync(token), CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default).Unwrap();
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _loading, 1, 0) != 0) return;
        try
        {
            var notes = await _source.GetAllAsync(cancellationToken).ConfigureAwait(false);
            foreach (var id in _registeredIds) _registry.Unregister(id);
            _registeredIds.Clear();
            lock (_gate) _loadErrors.Clear();

            var activeKeys = new Dictionary<CodeBrainLanguage, HashSet<string>>();
            foreach (var note in notes)
            {
                var language = ParseLanguage(note.Language);
                var source = note.Content;
                if (string.IsNullOrWhiteSpace(source))
                {
                    RecordError(note.Id, "代码源码为空");
                    continue;
                }
                if (!_compilers.TryGetValue(language, out var compiler))
                {
                    RecordError(note.Id, $"未注册 {language} 代码大脑编译器");
                    continue;
                }

                var sourceKey = SourceKey(language, source);
                if (!activeKeys.TryGetValue(language, out var keys))
                {
                    keys = new HashSet<string>(StringComparer.Ordinal);
                    activeKeys[language] = keys;
                }
                keys.Add(sourceKey);

                if (compiler.TryCompile(source, out var compilation, out var error) && compilation is not null)
                {
                    _registry.Register(note.Id, compilation.Create);
                    _registeredIds.Add(note.Id);
                }
                else
                {
                    RecordError(note.Id, error ?? "未知编译错误");
                }
            }

            foreach (var compiler in _compilers.Values)
                compiler.UnloadStale(activeKeys.GetValueOrDefault(compiler.Language) ?? new HashSet<string>());
            Loaded?.Invoke();
        }
        finally
        {
            Interlocked.Exchange(ref _loading, 0);
        }
    }

    private void RecordError(string id, string error)
    {
        lock (_gate) _loadErrors[id] = error;
        _registry.RecordError(id, error);
        _logger.LogWarning("Code brain compilation failed for {Id}: {Error}", id, error);
    }

    public static CodeBrainLanguage ParseLanguage(string? value)
        => string.Equals(value, "fsharp", StringComparison.OrdinalIgnoreCase)
            ? CodeBrainLanguage.FSharp
            : CodeBrainLanguage.CSharp;

    public static string SourceKey(CodeBrainLanguage language, string source)
        => $"{language}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))}";
}
