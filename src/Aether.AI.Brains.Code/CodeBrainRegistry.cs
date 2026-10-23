using Aether.AI.Brains.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aether.AI.Brains.Code;

/// <summary>
/// 代码大脑注册表：定义 Id → <see cref="IBrain"/> 工厂。源码编译服务或宿主可注册预编译工厂，
/// <see cref="CodeBrainFactory"/> 按定义 Id 取得 fresh 实例。
/// 工厂而非单例——代码大脑可有状态，每次召唤需新实例。
/// <para>编译失败按 Id 记进 <see cref="LoadErrors"/>，召唤时回兜底提示。</para>
/// </summary>
public interface ICodeBrainRegistry
{
    /// <summary>注册一个代码大脑工厂（覆盖同 Id 旧值）。<paramref name="factory"/> 每次调用须返回新实例。</summary>
    void Register(string id, Func<IBrain> factory);

    /// <summary>注销一个 Id（及其编译错误记录）。</summary>
    void Unregister(string id);

    /// <summary>记录一个 Id 的编译错误（召唤时回退用）。</summary>
    void RecordError(string id, string error);

    /// <summary>按 Id 取大脑：命中工厂返回 true + 新实例；编译失败返回 false + 错误；未注册返回 false + null 错误。</summary>
    bool TryCreate(string id, out IBrain? brain, out string? error);

    /// <summary>当前已注册的 Id 集合（供全量重载时先清后注）。</summary>
    IReadOnlyCollection<string> RegisteredIds { get; }
}

/// <inheritdoc/>
public sealed class CodeBrainRegistry : ICodeBrainRegistry
{
    private readonly Dictionary<string, Func<IBrain>> _factories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);
    private readonly ILogger<CodeBrainRegistry>? _logger;
    private readonly object _gate = new();

    public CodeBrainRegistry(ILogger<CodeBrainRegistry>? logger = null) => _logger = logger;

    public void Register(string id, Func<IBrain> factory)
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

    public bool TryCreate(string id, out IBrain? brain, out string? error)
    {
        Func<IBrain>? factory;
        lock (_gate)
        {
            if (_errors.TryGetValue(id, out var err))
            {
                brain = null;
                error = err;
                return false;
            }
            _factories.TryGetValue(id, out factory);
        }

        if (factory is null)
        {
            brain = null;
            error = null;
            return false;
        }

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

    public IReadOnlyCollection<string> RegisteredIds
    {
        get
        {
            lock (_gate) { return _factories.Keys.ToArray(); }
        }
    }
}
