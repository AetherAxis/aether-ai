using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Aether.AI.Harness.Agents;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Aether.AI.Harness.Scripting;

/// <summary>
/// Roslyn 动态编译代码规则源码 → <see cref="CodeRuleBrain"/> 实例。镜像 Trading 的
/// <c>StrategyScriptCompiler</c>：批量引用 AppDomain 已加载程序集 → 内存 emit → 装进可卸载 ALC
/// → 找 <see cref="CodeRuleBrain"/> 子类 → <see cref="Activator.CreateInstance"/>。编译失败返回错误。
/// <para>本类不依赖 <c>Acme.Trading.Scripting</c>（platform 不引 business）——复用模式，不复用代码。
/// 源码 hash 缓存 + 可卸载 ALC：重加载命中即复用（免重复编译），旧版本经 UnloadStale 回收（免默认 ALC 无界堆积）。</para>
/// </summary>
public static class RuleScriptCompiler
{
    // 源码 hash → 已编译程序集（装在可卸载 ALC 内）。
    private static readonly ConcurrentDictionary<string, LoadedAssembly> _cache = new();

    /// <summary>编译源码并创建 <see cref="CodeRuleBrain"/> 实例。成功 out instance；失败 out error。
    /// 命中缓存复用既有程序集（不重编译、不新装 ALC）；未命中则编译进可卸载 ALC 并缓存。</summary>
    public static bool TryCompile(string source, out CodeRuleBrain? instance, out string? error)
    {
        instance = null;
        error = null;

        var hash = HashOf(source);
        if (_cache.TryGetValue(hash, out var cached))
            return ExtractInstance(cached.Assembly, out instance, out error);

        if (!Compile(source, out var loaded, out error))
            return false;
        _cache[hash] = loaded;
        return ExtractInstance(loaded.Assembly, out instance, out error);
    }

    /// <summary>卸载当前源码 hash 集合之外的所有缓存程序集（重加载后清已删除/改名的旧版本）。
    /// <see cref="AssemblyLoadContext.Unload"/> 是异步的——运行中的旧实例保持其 ALC 存活直到自身释放。</summary>
    public static void UnloadStale(IReadOnlySet<string> currentHashes)
    {
        foreach (var kv in _cache)
        {
            if (currentHashes.Contains(kv.Key)) continue;
            if (!_cache.TryRemove(kv.Key, out var removed)) continue;
            try { removed.Alc.Unload(); }
            catch { /* 已在卸载中/竞态，忽略——下一轮会重编译替换 */ }
        }
    }

    /// <summary>源码 SHA256 hash（缓存键）。供脚本服务算本轮 currentHashes 集合调 <see cref="UnloadStale"/>。</summary>
    internal static string HashOf(string source)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));

    private static bool Compile(string source, out LoadedAssembly loaded, out string? error)
    {
        loaded = default;
        error = null;

        // 引用当前 AppDomain 已加载程序集（含 Aether.AI.Harness.dll 的 CodeRuleBrain + System.Runtime/Linq 等）
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Distinct()
            .ToArray();

        var compilation = CSharpCompilation.Create(
            "CodeRuleScript_" + System.Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(source) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        if (!result.Success)
        {
            error = string.Join("\n", result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString()));
            return false;
        }

        ms.Position = 0;
        // 装进可卸载 ALC（而非 Assembly.Load 进默认 ALC）——旧版本可经 UnloadStale 回收，不永久驻留。
        var alc = new CollectibleAlc();
        var asm = alc.LoadFromStream(ms);
        loaded = new LoadedAssembly(asm, alc);
        return true;
    }

    private static bool ExtractInstance(Assembly asm, out CodeRuleBrain? instance, out string? error)
    {
        instance = null;
        error = null;
        Type? brainType;
        try
        {
            brainType = asm.GetTypes().FirstOrDefault(t =>
                typeof(CodeRuleBrain).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
        }
        catch (ReflectionTypeLoadException ex)
        {
            // 类型加载失败也走「编译失败」路径（TryCompile 契约：失败 out error，不抛异常）——
            // 否则一条坏笔记的异常会逃逸到调用方，中断整轮 LoadAsync 循环（后续规则不注册、RulesLoaded 不触发）。
            error = "类型加载失败: " + string.Join("; ", ex.LoaderExceptions.Select(e => e?.Message ?? e!.GetType().Name));
            return false;
        }
        if (brainType is null)
        {
            error = "源码中未找到继承 CodeRuleBrain 的非抽象类";
            return false;
        }

        try
        {
            instance = (CodeRuleBrain)Activator.CreateInstance(brainType)!;
            return true;
        }
        catch (Exception ex)
        {
            // ctor 抛异常同理——按编译失败处理并反馈错误，不让调用方整轮中断。
            // Activator.CreateInstance 把 ctor 异常包进 TargetInvocationException（Message 是通用包装
            // 文案），拆包取 InnerException 反馈真实原因。
            var cause = ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex;
            error = $"实例化 {brainType.Name} 失败: {cause.Message}";
            return false;
        }
    }

    // 可卸载 ALC：isCollectible=true 让 Unload() 可回收其程序集。Load 返回 null →
    // 全部依赖（System.*、Aether.AI.Harness 等）走默认 ALC 解析，保证 CodeRuleBrain 类型身份跨 ALC 一致
    // （IsAssignableFrom 成立）；只有编译出的规则程序集本身在本 ALC，卸载干净不牵连共享依赖。
    // internal：同程序集的 FsRuleScriptCompiler（F# 编译器）复用同一 ALC 语义（仅此一处消费）。
    internal sealed class CollectibleAlc : AssemblyLoadContext
    {
        public CollectibleAlc() : base(isCollectible: true) { }
        protected override Assembly? Load(AssemblyName assemblyName) => null;
    }

    internal readonly record struct LoadedAssembly(Assembly Assembly, CollectibleAlc Alc);
}
