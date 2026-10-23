using Aether.AI.Harness.Agents;
using Microsoft.Extensions.Logging;

namespace Aether.AI.Harness.Scripting;

/// <summary>
/// 代码规则脚本加载服务实现。扫描 CodeRule 笔记 → Roslyn 编译 → 注册工厂进 <see cref="ICodeBrainRegistry"/>。
/// 通过 <see cref="ICodeNoteSource"/> 端口读笔记（不直接依赖 Notes.Application）；host 订阅 NotesChanged
/// 后调 <see cref="Reload"/>。镜像 Trading 的 <c>StrategyScriptingService</c>。
/// <para>按笔记 <c>Id</c> 注册（与 <c>AgentDefinition.Id</c> 一致），不按名称——名字可改/可重名，Id 稳定。</para>
/// </summary>
public sealed class RuleScriptingService : IRuleScriptingService
{
    private readonly ICodeNoteSource _source;
    private readonly ICodeBrainRegistry _registry;
    private readonly ILogger<RuleScriptingService> _logger;
    private readonly Dictionary<string, string> _loadErrors = new();
    private readonly HashSet<string> _registeredIds = new(); // 本服务动态注册的 Id，重加载时先清
    private readonly object _lock = new();
    private int _loading; // 防并发重入
    private CancellationTokenSource? _debounceCts;

    public event Action? RulesLoaded;

    public IReadOnlyDictionary<string, string> LoadErrors
    {
        get { lock (_lock) return new Dictionary<string, string>(_loadErrors); }
    }

    public RuleScriptingService(ICodeNoteSource source, ICodeBrainRegistry registry, ILogger<RuleScriptingService> logger)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger;
    }

    /// <summary>host 订阅 NotesChanged 后调用此方法触发防抖重加载（500ms）。</summary>
    public void Reload()
    {
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var ct = _debounceCts.Token;
        _ = Task.Delay(500, ct).ContinueWith(_ => LoadAsync(ct), TaskScheduler.Default);
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        // 防并发重入
        if (Interlocked.CompareExchange(ref _loading, 1, 0) != 0) return;
        try
        {
            var notes = await _source.GetAllAsync(cancellationToken);

            // 先 unregister 上次动态注册的（清理已删除的代码规则，避免召唤时残留旧工厂）
            foreach (var id in _registeredIds) _registry.Unregister(id);
            _registeredIds.Clear();
            lock (_lock) _loadErrors.Clear();

            // 本轮编译的代码规则源码 hash 集合——扫完后调 UnloadStale 回收已删除/改名/改坏的旧版本 ALC。
            // C#/F# 两个编译器各持独立缓存但共享同一 SHA256 hash 集（同源码内容同 hash，语言互斥无冲突）。
            var currentHashes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var note in notes)
            {
                if (string.IsNullOrWhiteSpace(note.Content))
                {
                    lock (_lock) _loadErrors[note.Id] = "代码源码为空";
                    _registry.RecordError(note.Id, "代码源码为空");
                    continue;
                }
                currentHashes.Add(RuleScriptCompiler.HashOf(note.Content!));
                // 按笔记语言分派编译器（front-matter language: 字段；默认 csharp）。
                IRuleBrain? instance;
                string? err;
                bool ok;
                if (string.Equals(note.Language, "fsharp", StringComparison.OrdinalIgnoreCase))
                {
                    ok = FsRuleScriptCompiler.TryCompile(note.Content!, out instance, out err);
                }
                else
                {
                    ok = RuleScriptCompiler.TryCompile(note.Content!, out var csBrain, out err);
                    instance = csBrain;
                }
                if (ok && instance is not null)
                {
                    var type = instance.GetType();
                    var id = note.Id;
                    // 工厂每次召唤返回 fresh 实例（代码大脑可有状态）。每步式 F# 大脑的 type 是
                    // FsRuleBrainAdapter<'T>（无参构造经 new() 约束重建 'T），与线性式同构。
                    _registry.Register(id, () => (IRuleBrain)Activator.CreateInstance(type)!);
                    _registeredIds.Add(id);
                    _logger.LogInformation("已加载代码规则脚本：{Title} ({Id})", note.Title, id);
                }
                else
                {
                    lock (_lock) _loadErrors[note.Id] = err ?? "未知编译错误";
                    _registry.RecordError(note.Id, err ?? "未知编译错误");
                    _logger.LogWarning("代码规则脚本编译失败 {Title}：{Err}", note.Title, err);
                }
            }
            // 回收本轮源码集合之外的旧编译产物（可卸载 ALC）——防长会话反复改代码规则致默认 ALC 无界堆积。
            RuleScriptCompiler.UnloadStale(currentHashes);
            FsRuleScriptCompiler.UnloadStale(currentHashes);
            RulesLoaded?.Invoke();
        }
        finally
        {
            Interlocked.Exchange(ref _loading, 0);
        }
    }
}
