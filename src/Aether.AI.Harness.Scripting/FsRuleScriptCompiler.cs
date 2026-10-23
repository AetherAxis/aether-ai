using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.FSharp;
using FSharp.Compiler.CodeAnalysis;
using FSharp.Compiler.Diagnostics;
using Microsoft.FSharp.Control;

namespace Aether.AI.Harness.Scripting;

/// <summary>
/// F# 动态编译代码规则源码 → <see cref="IRuleBrain"/> 实例（F# 作者契约的运行时入口）。镜像
/// <see cref="RuleScriptCompiler"/>（C# Roslyn 路径）的整套模式：源码 hash 缓存 + 可卸载 ALC +
/// <see cref="UnloadStale"/> + 「失败 out error 不抛异常」契约。差异仅在编译器本体：
/// <list type="bullet">
/// <item>FCS（<c>FSharp.Compiler.Service</c>）的 <c>FSharpChecker.Compile</c> 是 fsc.exe 参数式且基于文件——
/// 源码/dll 走临时文件（<c>%TMP%/aether-fs/</c>），编译成功后读字节进可卸载 ALC，临时文件即删。</item>
/// <item>引用集 = AppDomain 已加载程序集 + <b>显式首个 -r:FSharp.Core</b>（取契约程序集 bin 同目录）——
/// FCS 不显式传会用其内置的远古默认 FSharp.Core（官方文档明示的坑）。</item>
/// <item>类型发现两条路：继承 <see cref="FsCodeRuleBrain"/>（线性流程式，复用 C# 协程驱动器，实例即
/// <see cref="IRuleBrain"/>）；实现 <see cref="IFsRuleBrain"/>（每步决策式）→ 包进
/// <c>FsRuleBrainAdapter&lt;'T&gt;</c>（泛型 + new() 约束 → 平台侧 Activator 无参构造可复建）。</item>
/// </list>
/// <para>checker 创建昂贵（加载整套 F# 编译器），进程级单例 + 懒加载。编译跑在后台加载线程
/// （<c>RuleScriptingService.LoadAsync</c>），无 SynchronizationContext，sync-over-async 安全。</para>
/// </summary>
public static class FsRuleScriptCompiler
{
    // 源码 hash → 已编译程序集（装在可卸载 ALC 内）。hash 键与 C# 编译器共用同一 SHA256（语义相同），
    // 但缓存各自独立（同一份源码不可能同时是合法 C# 与合法 F#，无冲突）。
    private static readonly ConcurrentDictionary<string, RuleScriptCompiler.LoadedAssembly> _cache = new();

    // F# 编译器服务实例（进程单例；创建 ~秒级）。FSharpChecker.Create 的 F# optional 参数在 C# 侧
    // 是必填 FSharpOption（14 个）——全传 null = None = 默认配置。
    private static readonly Lazy<FSharpChecker> _checker = new(() => FSharpChecker.Create(
        null, null, null, null, null, null, null, null, null, null, null, null, null, null));

    /// <summary>临时编译目录（源码 + dll 落盘走 FCS 文件式 API；编译后即删）。</summary>
    private static readonly string TmpDir = Path.Combine(Path.GetTempPath(), "aether-fs");

    /// <summary>FSharp.Core 路径：契约程序集（Aether.AI.Harness.FSharp）bin 同目录。构造期引用
    /// typeof(<see cref="IFsRuleBrain"/>) 顺带把契约程序集加载进 AppDomain（供引用扫描收录）。</summary>
    private static readonly string FSharpCorePath = Path.Combine(
        Path.GetDirectoryName(typeof(IFsRuleBrain).Assembly.Location)!,
        "FSharp.Core.dll");

    /// <summary>targeting pack ref 目录（多根候选，见 <see cref="FindRefPackDir"/>——framework-dependent 走 SDK
    /// packs/，自包含 .app 走 NuGet 缓存兜底）。懒定位；全候选 miss 时 null → TryCompile 返回友好错误。</summary>
    private static readonly Lazy<string?> RefPackDir = new(FindRefPackDir);

    /// <summary>编译 F# 源码并创建 <see cref="IRuleBrain"/> 实例（线性式=FsCodeRuleBrain 子类；
    /// 每步式=FsRuleBrainAdapter 包装）。成功 out instance；失败 out error（不抛异常——镜像 C# 编译器契约）。
    /// 命中缓存复用既有程序集（不重编译、不新装 ALC）。</summary>
    public static bool TryCompile(string source, out IRuleBrain? instance, out string? error)
    {
        instance = null;
        error = null;

        var hash = RuleScriptCompiler.HashOf(source);
        if (_cache.TryGetValue(hash, out var cached))
            return ExtractInstance(cached.Assembly, out instance, out error);

        if (!Compile(source, out var loaded, out error))
            return false;
        _cache[hash] = loaded;
        return ExtractInstance(loaded.Assembly, out instance, out error);
    }

    /// <summary>卸载当前源码 hash 集合之外的所有缓存程序集（重加载后清已删除/改名的旧版本）。
    /// <see cref="AssemblyLoadContext.Unload"/> 是异步的——运行中的旧实例保持其 ALC 存活到自身释放。</summary>
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

    private static bool Compile(string source, out RuleScriptCompiler.LoadedAssembly loaded, out string? error)
    {
        loaded = default;
        error = null;

        var refPack = RefPackDir.Value;
        if (refPack is null)
        {
            error = "未找到 .NET targeting pack（Microsoft.NETCore.App.Ref）——F# 代码规则需要 .NET 10 SDK，"
                + "或设置 DOTNET_ROOT 指向 dotnet 安装目录，或在构建过本项目的机器上运行（NuGet 缓存含该包）。";
            return false;
        }

        Directory.CreateDirectory(TmpDir);
        var id = System.Guid.NewGuid().ToString("N");
        // .fsx：脚本模式允许源码不带 namespace/module 声明（库模式 .fs 强制要求——作者免写样板），
        // 且支持 #r 指令（高级用户可自加引用）。labs 文件名不受影响（内容才是编译输入）。
        var srcPath = Path.Combine(TmpDir, "FsRule_" + id + ".fsx");
        var dllPath = Path.Combine(TmpDir, "FsRule_" + id + ".dll");
        try
        {
            File.WriteAllText(srcPath, source);

            var argv = BuildArgs(srcPath, dllPath, refPack);
            // sync-over-async：加载路径跑在后台线程（无 SynchronizationContext），RunSynchronously 安全。
            // userOpName null = F# None。
            var result = FSharpAsync.RunSynchronously(_checker.Value.Compile(argv.ToArray(), null), null, null);
            var diagnostics = result.Item1;          // FSharpDiagnostic[]（编译诊断）
            var compileExn = result.Item2;           // FSharpOption<Exception>：非 null = 编译器内部异常

            var errors = diagnostics
                .Where(d => d.Severity == FSharpDiagnosticSeverity.Error)
                .Select(d => d.ToString())
                .ToArray();
            if (compileExn is not null || errors.Length > 0)
            {
                // fsc 的 "Processing of a script fragment has stopped…" 是通用包装——同时输出
                // 异常链（含 InnerException）与 Error 诊断，真实原因必居其一。
                var sb = new System.Text.StringBuilder();
                if (compileExn is not null)
                {
                    sb.Append("fsc 内部错误: ");
                    for (Exception? e = compileExn.Value; e is not null; e = e.InnerException)
                    {
                        sb.Append(e.Message);
                        if (e.InnerException is not null) sb.Append(" → ");
                    }
                }
                if (errors.Length > 0)
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.Append(string.Join("\n", errors));
                }
                error = sb.Length > 0 ? sb.ToString() : "未知 F# 编译错误";
                return false;
            }

            // 读字节 → 可卸载 ALC（与临时文件解耦，加载后即删盘）。
            var bytes = File.ReadAllBytes(dllPath);
            using var ms = new MemoryStream(bytes);
            var alc = new RuleScriptCompiler.CollectibleAlc();
            var asm = alc.LoadFromStream(ms);
            loaded = new RuleScriptCompiler.LoadedAssembly(asm, alc);
            return true;
        }
        catch (Exception ex)
        {
            error = "F# 编译失败: " + ex.Message;
            return false;
        }
        finally
        {
            try { File.Delete(srcPath); } catch { /* 临时文件清理失败不阻断 */ }
            try { File.Delete(dllPath); } catch { }
        }
    }

    /// <summary>fsc.exe 参数式命令行：输出 dll + 库目标 + 源文件 + 引用集（FSharp.Core 显式置首）。
    /// <para><b>--noframework（关键）</b>：FCS 的默认框架引用表是 .NET Framework 时代的
    /// （System.Runtime.Remoting / System.Windows.Forms 等）——在 .NET 10 上不存在 → 不加此 flag
    /// 启动即报「程序集引用 xxx.dll 未找到」。跳过默认表后引用集 <b>SDK 等价配方</b>（探针验证）：</para>
    /// <para>ref-pack 全集 + bin 应用程序集。不能只用运行时 impl 程序集——fsc 的系统类型检查要求
    /// 「定义」系统类型的程序集（ref-pack 契约程序集定义 System.Array 等；impl System.Runtime 是
    /// 纯转发 facade，过不了检查，且 facade→CoreLib 的转发在 typecheck 报 List`1 不可用）。</para>
    /// </summary>
    private static List<string> BuildArgs(string srcPath, string dllPath, string refPackDir)
    {
        var argv = new List<string>
        {
            "fsc.exe",
            "-o", dllPath,
            "--target:library",
            "--nologo",
            "--noframework",
            srcPath,
            // FSharp.Core 显式首个 -r：FCS 内置默认引用是远古版本，不传会绑错 FSharp.Core。
            "-r:" + FSharpCorePath,
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "FSharp.Core" };
        // ref-pack 全集（框架契约）在前；bin 应用程序集在后，与 ref-pack 重名的跳过（先到先得）。
        foreach (var r in Directory.GetFiles(refPackDir, "*.dll"))
        {
            if (seen.Add(Path.GetFileNameWithoutExtension(r)))
                argv.Add("-r:" + r);
        }
        foreach (var r in AppAssemblyPaths())
        {
            if (seen.Add(Path.GetFileNameWithoutExtension(r)))
                argv.Add("-r:" + r);
        }
        return argv;
    }

    /// <summary>AppDomain 已加载的非框架程序集（应用/NuGet：契约程序集 / Harness 的 FsCodeRuleBrain、
    /// IFsRuleBrain / MEAI 等）。排除共享框架 impl 目录与 System.Private.CoreLib——框架引用由 ref-pack 提供
    /// （impl facade 会破坏 fsc 的系统类型解析，见 <see cref="BuildArgs"/>；CoreLib 是转发目标、与 ref-pack
    /// 契约重复定义系统类型）。<b>自包含运行时（打包 .app）下全部程序集都在 bundle 内</b>——按运行时目录上溯
    /// 推导 shared 根会把应用程序集也排除掉，故按<b>路径签名</b>（相邻 <c>shared/Microsoft.*</c> 段，SDK 布局特征）
    /// 判；bundle 内的 System.* impl 由 ref-pack 同名去重兜底（BuildArgs 的 seen 集合先到先得）。</summary>
    private static IEnumerable<string> AppAssemblyPaths()
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (a.IsDynamic || string.IsNullOrEmpty(a.Location)) continue;
            if (Path.GetFileNameWithoutExtension(a.Location) == "System.Private.CoreLib") continue;
            if (IsSharedFrameworkPath(a.Location)) continue;
            yield return a.Location;
        }
    }

    /// <summary>路径是否位于共享框架 impl 目录（含相邻 <c>shared/Microsoft.&lt;fx&gt;/&lt;版本&gt;</c> 路径段——
    /// SDK 布局签名（第三段以数字开头，防用户目录碰巧叫 shared/Microsoft.* 的误伤）；自包含 bundle 内
    /// 无此段 → 不排除）。</summary>
    private static bool IsSharedFrameworkPath(string location)
    {
        var segs = location.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var i = 0; i + 2 < segs.Length; i++)
        {
            if (string.Equals(segs[i], "shared", StringComparison.OrdinalIgnoreCase)
                && segs[i + 1].StartsWith("Microsoft.", StringComparison.Ordinal)
                && segs[i + 2].Length > 0 && char.IsDigit(segs[i + 2][0]))
                return true;
        }
        return false;
    }

    /// <summary>定位 targeting pack 的 ref 目录（<c>&lt;ver&gt;/ref/&lt;tfm&gt;/</c>，含 System.Runtime.dll）。
    /// public 供测试断言/诊断。多根候选按序探测（探针定案：任何同 major 的 pack 契约都能让 fsc 编译，
    /// 版本无需精确匹配运行时）：①框架依赖运行时根（dotnet run/test）②NuGet 全局缓存（本仓 PackageReference
    /// 钉的 Microsoft.NETCore.App.Ref，restore 必下载——<b>自包含 .app 的兜底主力</b>：bundle 内运行时目录
    /// 指向 app 自身，上溯找不到 SDK packs/）③DOTNET_ROOT（含架构变体）④常见安装根。</summary>
    public static string? FindRefPackDir()
    {
        foreach (var versionsRoot in CandidateVersionsRoots())
        {
            var hit = FindHighestRefDir(versionsRoot);
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>含版本子目录的根（已存在、去重、保序）：各 dotnet 根下的 packs/Microsoft.NETCore.App.Ref + NuGet 包目录。</summary>
    private static IEnumerable<string> CandidateVersionsRoots()
    {
        var roots = new List<string>();
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string full;
            try { full = Path.GetFullPath(path!); } catch { return; }
            if (!Directory.Exists(full)) return;
            if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase)) roots.Add(full);
        }

        foreach (var root in CandidateDotnetRoots())
            Add(Path.Combine(root, "packs", "Microsoft.NETCore.App.Ref"));
        Add(NuGetRefPackDir());
        return roots;
    }

    private static IEnumerable<string> CandidateDotnetRoots()
    {
        // ① 框架依赖运行时根：runtimeDir = <root>/shared/<fx>/<ver>/ → 上溯 3 级。
        //    自包含运行时此处指向 bundle 内 → 加不出 packs（由 ② NuGet 缓存兜底）。
        string? runtimeDir = null;
        try { runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(); }
        catch { }
        if (!string.IsNullOrEmpty(runtimeDir))
            yield return Path.Combine(runtimeDir, "..", "..", "..");

        // ② 环境变量（DOTNET_ROOT 架构变体是 SDK 官方约定）+ dotnet-install 用户级。
        yield return Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? string.Empty;
        yield return Environment.GetEnvironmentVariable("DOTNET_ROOT(x64)") ?? string.Empty;
        yield return Environment.GetEnvironmentVariable("DOTNET_ROOT(arm64)") ?? string.Empty;
        yield return Environment.GetEnvironmentVariable("DOTNET_INSTALL_DIR") ?? string.Empty;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile)) yield return Path.Combine(profile, ".dotnet");

        // ③ 常见安装根（macOS x64 pkg / macOS arm64 Homebrew / Linux 发行版 / Windows）。
        yield return "/usr/local/share/dotnet";
        yield return "/opt/homebrew/share/dotnet";
        yield return "/usr/share/dotnet";
        yield return "/usr/lib/dotnet";
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles)) yield return Path.Combine(programFiles, "dotnet");
    }

    /// <summary>NuGet 全局缓存里的 Microsoft.NETCore.App.Ref 包目录（本仓 Scripting csproj 钉了
    /// ExcludeAssets=all 的 PackageReference——restore 保证它在任何构建过本仓的机器上都存在）。</summary>
    private static string? NuGetRefPackDir()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile)
            ? null
            : Path.Combine(profile, ".nuget", "packages", "microsoft.netcore.app.ref");
    }

    /// <summary>取最高的同 major 版本目录下的 ref/&lt;tfm&gt; 子目录（须含 System.Runtime.dll）。
    /// 只认与运行时同 major（如 net10）的版本——排除装着的 11 预览等（跨 major 契约会绑出 11.0.0.0 的
    /// System.Runtime 引用，net10 运行时解析不到）。</summary>
    private static string? FindHighestRefDir(string versionsRoot)
    {
        try
        {
            var majorPrefix = Environment.Version.Major + ".";
            foreach (var versionDir in Directory.GetDirectories(versionsRoot)
                         .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
            {
                if (!Path.GetFileName(versionDir).StartsWith(majorPrefix, StringComparison.Ordinal)) continue;
                var tfm = Directory.GetDirectories(Path.Combine(versionDir, "ref"))
                    .OrderByDescending(t => Path.GetFileName(t), StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(t => File.Exists(Path.Combine(t, "System.Runtime.dll")));
                if (tfm is not null) return tfm;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool ExtractInstance(Assembly asm, out IRuleBrain? instance, out string? error)
    {
        instance = null;
        error = null;

        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            // 类型加载失败也走「编译失败」路径（TryCompile 契约：失败 out error，不抛异常）——
            // 否则一条坏笔记的异常会逃逸到调用方，中断整轮 LoadAsync 循环。
            error = "类型加载失败: " + string.Join("; ", ex.LoaderExceptions.Select(e => e?.Message ?? e!.GetType().Name));
            return false;
        }

        // 线性流程式：继承 FsCodeRuleBrain（即 IRuleBrain——C# 协程驱动器驱动）。
        var linear = types.FirstOrDefault(t =>
            typeof(FsCodeRuleBrain).IsAssignableFrom(t) && !t.IsAbstract);
        if (linear is not null)
            return CreateInstance(linear, out instance, out error);

        // 每步决策式：实现 IFsRuleBrain → 包进 FsRuleBrainAdapter<'T>（平台侧 Activator 经无参构造复建）。
        var stepwise = types.FirstOrDefault(t =>
            typeof(IFsRuleBrain).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
        if (stepwise is not null)
        {
            if (stepwise.GetConstructor(Type.EmptyTypes) is null)
            {
                error = $"F# 大脑类 {stepwise.Name} 需要无参构造";
                return false;
            }
            var adapterType = typeof(FsRuleBrainAdapter<>).MakeGenericType(stepwise);
            return CreateInstance(adapterType, out instance, out error);
        }

        error = "源码中未找到继承 FsCodeRuleBrain 或实现 IFsRuleBrain 的非抽象类";
        return false;
    }

    private static bool CreateInstance(Type type, out IRuleBrain? instance, out string? error)
    {
        instance = null;
        error = null;
        try
        {
            instance = (IRuleBrain)Activator.CreateInstance(type)!;
            return true;
        }
        catch (Exception ex)
        {
            // ctor 抛异常同理——按编译失败处理并反馈错误，不让调用方整轮中断（镜像 C# 编译器的回归守卫）。
            var cause = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            error = $"实例化 {type.Name} 失败: {cause.Message}";
            return false;
        }
    }
}
