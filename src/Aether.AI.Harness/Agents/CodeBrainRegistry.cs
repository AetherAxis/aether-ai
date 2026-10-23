using Microsoft.Extensions.Logging;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// 代码大脑注册表：笔记 Id → <see cref="IRuleBrain"/> 工厂。<see cref="RuleScriptingService"/>（Harness.Scripting）
/// 即时编译 C# 笔记后把工厂注册进来；<see cref="AgentFactory.BuildRuleAgent"/> 按笔记 Id 取工厂得 fresh 实例。
/// 工厂而非单例——代码大脑可有状态，每次召唤需新实例。
/// <para>编译失败按 Id 记进 <see cref="LoadErrors"/>，召唤时回兜底提示。</para>
/// </summary>
public interface ICodeBrainRegistry
{
    /// <summary>注册一个代码大脑工厂（覆盖同 Id 旧值）。<paramref name="factory"/> 每次调用须返回新实例。</summary>
    void Register(string id, Func<IRuleBrain> factory);

    /// <summary>注销一个 Id（及其编译错误记录）。</summary>
    void Unregister(string id);

    /// <summary>记录一个 Id 的编译错误（召唤时回退用）。</summary>
    void RecordError(string id, string error);

    /// <summary>按 Id 取大脑：命中工厂返回 true + 新实例；编译失败返回 false + 错误；未注册返回 false + null 错误。</summary>
    bool TryCreate(string id, out IRuleBrain? brain, out string? error);

    /// <summary>当前已注册的 Id 集合（供全量重载时先清后注）。</summary>
    IReadOnlyCollection<string> RegisteredIds { get; }
}

/// <inheritdoc/>
public sealed class CodeBrainRegistry : ICodeBrainRegistry
{
    private readonly Dictionary<string, Func<IRuleBrain>> _factories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);
    private readonly ILogger<CodeBrainRegistry>? _logger;
    private readonly object _gate = new();

    public CodeBrainRegistry(ILogger<CodeBrainRegistry>? logger = null) => _logger = logger;

    public void Register(string id, Func<IRuleBrain> factory)
    {
        if (string.IsNullOrEmpty(id)) throw new ArgumentException("Id 不能为空", nameof(id));
        ArgumentNullException.ThrowIfNull(factory);
        lock (_gate)
        {
            _factories[id] = factory;
            _errors.Remove(id);
        }
    }

    public void Unregister(string id)
    {
        lock (_gate)
        {
            _factories.Remove(id);
            _errors.Remove(id);
        }
    }

    public void RecordError(string id, string error)
    {
        if (string.IsNullOrEmpty(id)) return;
        lock (_gate)
        {
            _errors[id] = error ?? "未知编译错误";
        }
    }

    public bool TryCreate(string id, out IRuleBrain? brain, out string? error)
    {
        lock (_gate)
        {
            if (_errors.TryGetValue(id, out var err))
            {
                brain = null;
                error = err;
                return false;
            }
            if (_factories.TryGetValue(id, out var factory))
            {
                try
                {
                    brain = factory();
                    error = null;
                    return true;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "代码大脑工厂实例化失败: {Id}", id);
                    brain = null;
                    error = $"代码大脑实例化失败：{ex.Message}";
                    return false;
                }
            }
        }
        brain = null;
        error = null;
        return false;
    }

    public IReadOnlyCollection<string> RegisteredIds
    {
        get
        {
            lock (_gate) { return _factories.Keys.ToArray(); }
        }
    }
}
