using System.Reflection;
using Aether.AI.Brains.Code;
using Aether.AI.Brains.Code.Compilation.CSharp;
using Aether.AI.Brains.Llm;
using Aether.AI.Brains.Rule;
using Aether.AI.Hosting;

namespace Aether.AI.Integration.Tests;

/// <summary>
/// 依赖边界：不同部署形态引入的包集合不同，任何一个形态都不该被别的形态的依赖污染
/// ——纯规则宿主不该被迫带上模型 SDK，预编译代码宿主不该被迫带上 Roslyn/FCS。
/// <para>
/// 这里直接走「程序集引用闭包」：从形态的代表程序集出发，递归读取 <see cref="Assembly.GetReferencedAssemblies"/>。
/// 之所以不靠某个专用消费项目，是因为闭包是<b>产品程序集自身的属性</b>，用普通 <c>dotnet test</c> 就能守住。
/// </para>
/// </summary>
public class DependencyClosureTests
{
    private static readonly string[] ModelSdk = ["OpenAI", "Microsoft.Agents.AI.OpenAI"];
    private static readonly string[] Compilers = ["Microsoft.CodeAnalysis", "FSharp.Compiler"];

    [Fact]
    public void Rule_only_host_pulls_no_model_sdk_and_no_compiler()
    {
        // 形态：AddRuleBrains + AddAetherAI —— 纯规则，无模型、无代码编译。
        var closure = ClosureOf(typeof(AetherAIServiceCollectionExtensions).Assembly, typeof(RuleProgramBrain).Assembly);

        AssertNoMatch(closure, [.. ModelSdk, .. Compilers]);
    }

    [Fact]
    public void Precompiled_code_host_pulls_no_model_sdk_and_no_compiler()
    {
        // 形态：AddCSharpCodeBrains + AddAetherAI —— 作者写好的代码大脑，不需要 Roslyn/FCS。
        var closure = ClosureOf(typeof(AetherAIServiceCollectionExtensions).Assembly, typeof(CodeBrainFactory).Assembly);

        AssertNoMatch(closure, [.. ModelSdk, .. Compilers]);
    }

    [Fact]
    public void Dynamic_csharp_host_may_pull_roslyn_but_never_the_model_sdk_or_fcs()
    {
        // 形态：AddCSharpBrainCompilation —— 允许 Roslyn，仍不允许模型 SDK / F# 编译器。
        var closure = ClosureOf(typeof(CSharpCodeBrainCompiler).Assembly);

        AssertNoMatch(closure, [.. ModelSdk, "FSharp.Compiler"]);
        Assert.Contains(closure, name => name.StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_walk_actually_sees_the_model_dependency_it_guards_against()
    {
        // 反向对照：LLM 包确实引用了 OpenAI。若闭包遍历失效（永远返回空集），上面几项会假绿。
        var closure = ClosureOf(typeof(LlmBrainFactory).Assembly);

        Assert.Contains(closure, name => name.StartsWith("OpenAI", StringComparison.OrdinalIgnoreCase));
    }

    // —— helpers ——

    private static void AssertNoMatch(IReadOnlySet<string> closure, string[] forbiddenPrefixes)
    {
        var violations = closure
            .Where(name => forbiddenPrefixes.Any(prefix =>
                name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(violations.Count == 0, $"依赖边界被破坏：{string.Join(", ", violations)}");
    }

    /// <summary>多根合并的引用闭包（含根自身）。解析不到的程序集只记名字、不再深入。</summary>
    private static IReadOnlySet<string> ClosureOf(params Assembly[] roots)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<Assembly>(roots);

        while (queue.Count > 0)
        {
            var assembly = queue.Dequeue();
            if (!seen.Add(assembly.GetName().Name!)) continue;

            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                var name = reference.Name;
                if (name is null || seen.Contains(name) || IsFrameworkNoise(name)) continue;

                var loaded = TryLoad(reference);
                if (loaded is null)
                {
                    seen.Add(name); // 解析不到（未随测试输出发布）——记下名字，无法继续深入
                    continue;
                }
                queue.Enqueue(loaded);
            }
        }

        return seen;
    }

    private static Assembly? TryLoad(AssemblyName reference)
    {
        try
        {
            return Assembly.Load(reference);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>框架/扩展库子树不参与遍历：它们不会引用模型 SDK 或编译器，跳过只为了缩短遍历。</summary>
    private static bool IsFrameworkNoise(string name)
        => name is "netstandard" or "mscorlib"
           || name.StartsWith("System", StringComparison.Ordinal)
           || name.StartsWith("Microsoft.Extensions", StringComparison.Ordinal);
}
