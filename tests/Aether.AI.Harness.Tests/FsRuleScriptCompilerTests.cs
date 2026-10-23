using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Scripting;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Tests;

/// <summary>
/// F# 代码规则大脑（<c>.coderule.fs</c> → FCS 编译 → <c>IRuleBrain</c>）测试。镜像
/// <see cref="CodeRuleBrainTests"/> 的端到端刺探，验证两条作者形态：
/// 每步决策式（<c>IFsRuleBrain</c> + <c>Step</c> DU，经 <c>FsRuleBrainAdapter</c> 包装）与
/// 线性流程式（<c>FsCodeRuleBrain</c> 继承 C# <see cref="CodeRuleBrain"/>，F# task CE 直接
/// <c>let!</c> C# 的 <c>CallAwaitable</c>——FS-1097 GetAwaiter 模式 + ICriticalNotifyCompletion）。
/// 同时锁定编译器契约：hash 缓存复用、可卸载 ALC 驱逐、失败 out error 不抛异常。
/// </summary>
public class FsRuleScriptCompilerTests
{
    // —— F# 源码样例（真实走 FCS 编译）——

    /// <summary>线性流程式（spike 核心）：F# task CE await C# CallAwaitable——FS-1097 互操作。</summary>
    private const string LinearEchoFs = @"
open System.Threading.Tasks
open Aether.AI.Harness.FSharp

type FsEchoBrain() =
    inherit FsCodeRuleBrain()
    override this.RunAsync() =
        task {
            let! r = this.Call(""Echo"", [(""text"", box this.UserText)])
            return ""fs结果:"" + r
        }
";

    /// <summary>线性多步链：两次 let! Call 顺序执行，第二次用第一次结果（协程状态跨挂起存活）。</summary>
    private const string LinearChainFs = @"
open System.Threading.Tasks
open Aether.AI.Harness.FSharp

type FsChainBrain() =
    inherit FsCodeRuleBrain()
    override this.RunAsync() =
        task {
            let mutable captured = this.UserText + ""_s0""
            let! first = this.Call(""Echo"", [(""text"", box captured)])
            captured <- captured + ""|"" + first
            let! second = this.Call(""Echo"", [(""text"", box first)])
            return captured + ""||"" + second
        }
";

    /// <summary>每步决策式：Step 0 调工具，之后用 LastResult 终答（经 FsRuleBrainAdapter 包装）。</summary>
    private const string StepwiseRouterFs = @"
open Aether.AI.Harness.FSharp

type FsRouter() =
    interface IFsRuleBrain with
        member _.Decide(ctx) =
            match ctx.Step with
            | 0 -> Call(""Echo"", [(""text"", box ctx.UserText)])
            | _ -> Reply(""fs回:"" + ctx.LastResult)
";

    private const string BrokenFs = @"
let broken = (
";

    private const string ThrowingCtorFs = @"
open System.Threading.Tasks
open Aether.AI.Harness.FSharp

type ThrowingFs() =
    inherit FsCodeRuleBrain()
    do failwith ""fs init boom""
    override this.RunAsync() = Task.FromResult ""never""
";

    // —— 编译器契约 ——

    [Fact]
    public void Fs_ref_pack_locator_finds_a_dir_with_system_runtime()
    {
        // 多根候选定位器（SDK packs/ 或 NuGet 缓存）——任何能构建/跑本测试的机器都须命中其一
        //（回归守卫：打包 .app 的自包含场景靠 NuGet 缓存兜底，见 FindRefPackDir 注释）。
        var dir = FsRuleScriptCompiler.FindRefPackDir();

        Assert.NotNull(dir);
        Assert.True(File.Exists(Path.Combine(dir!, "System.Runtime.dll")), $"pack 目录缺 System.Runtime.dll: {dir}");
    }

    [Fact]
    public async Task Fs_compile_stepwise_success()
    {
        var ok = FsRuleScriptCompiler.TryCompile(StepwiseRouterFs, out var brain, out var error);

        Assert.True(ok, error ?? "(no error)");
        Assert.NotNull(brain);
        Assert.Null(error);

        // 第一步：产出工具调用（Call）。
        var ctx1 = new RuleContext { LatestUser = new ChatMessage(ChatRole.User, "hello") };
        var output1 = await brain!.DecideAsync(ctx1, default);
        var call = Assert.Single(output1.OfType<FunctionCallContent>());
        Assert.Equal("Echo", call.Name);

        // 第二步：喂工具结果 → 用 LastResult 终答。
        var ctx2 = new RuleContext
        {
            LatestUser = new ChatMessage(ChatRole.User, "hello"),
            LatestToolResult = new FunctionResultContent(call.CallId, "echo:hello"),
            ToolCallsThisTurn = 1,
        };
        var output2 = await brain.DecideAsync(ctx2, default);
        Assert.Equal("fs回:echo:hello", Assert.Single(output2.OfType<TextContent>()).Text);
    }

    [Fact]
    public void Fs_compile_syntax_error_returns_error_not_exception()
    {
        var ok = FsRuleScriptCompiler.TryCompile(BrokenFs, out var brain, out var error);

        Assert.False(ok);
        Assert.Null(brain);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Fs_compile_ctor_throw_returns_error_not_exception()
    {
        // 回归守卫（镜像 C# 编译器契约）：ctor 抛异常按编译失败反馈，不逃逸中断整轮 LoadAsync。
        var ok = FsRuleScriptCompiler.TryCompile(ThrowingCtorFs, out var brain, out var error);

        Assert.False(ok);
        Assert.Null(brain);
        Assert.NotNull(error);
        Assert.Contains("fs init boom", error!);
    }

    [Fact]
    public void Fs_compile_cache_reuses_assembly()
    {
        // 同源码二次编译复用同一程序集（不重编译、不新装 ALC）——镜像 StrategyScriptCompilerCacheTests。
        Assert.True(FsRuleScriptCompiler.TryCompile(StepwiseRouterFs, out var a, out var errA), errA);
        Assert.True(FsRuleScriptCompiler.TryCompile(StepwiseRouterFs, out var b, out var errB), errB);

        Assert.Same(CompiledAssemblyOf(a!), CompiledAssemblyOf(b!));
    }

    [Fact]
    public void Fs_compile_unload_stale_evicts()
    {
        // 编译 A → UnloadStale(空集) 驱逐 → 再编译 A 得新程序集（证明 ALC 回收后可重建）。
        Assert.True(FsRuleScriptCompiler.TryCompile(StepwiseRouterFs, out var first, out var e1), e1);
        FsRuleScriptCompiler.UnloadStale(new HashSet<string>(StringComparer.Ordinal));
        Assert.True(FsRuleScriptCompiler.TryCompile(StepwiseRouterFs, out var second, out var e2), e2);

        Assert.NotSame(CompiledAssemblyOf(first!), CompiledAssemblyOf(second!));
    }

    /// <summary>取大脑实例的「编译产物程序集」：每步式实例是 FsRuleBrainAdapter&lt;'T&gt;——泛型类型定义
    /// 在契约程序集，编译产物是其类型实参（用户 F# 类）所在的程序集；线性式实例类型即编译产物。</summary>
    private static System.Reflection.Assembly CompiledAssemblyOf(Aether.AI.Harness.Agents.IRuleBrain brain)
    {
        var t = brain.GetType();
        return t.IsGenericType ? t.GetGenericArguments()[0].Assembly : t.Assembly;
    }

    // —— F# 大脑驱动 MAF agent 循环（端到端）——

    [Fact]
    public async Task Fs_linear_brain_drives_agent_loop()
    {
        // spike 核心端到端：F# task CE 挂起在 C# CallAwaiter → MAF 执行工具 → 结果回喂 → F# 协程恢复 → 终答。
        Assert.True(FsRuleScriptCompiler.TryCompile(LinearEchoFs, out var brain, out var error), error);
        var echo = AIFunctionFactory.Create((Func<string, string>)Echo);
        var client = new RuleChatClient(brain!);
        var options = new HarnessAgentOptions
        {
            Id = "fs-rule-spike",
            Name = "FsRuleSpike",
            Description = "f# rule agent spike",
            // 规则大脑不消费注入式 NL 上下文——关 TodoProvider，否则它追加 todo user 消息污染 LatestUser。
            DisableTodoProvider = true,
            ChatOptions = new ChatOptions { Instructions = "code", Tools = new List<AITool> { echo } },
        };
        var agent = client.AsHarnessAgent(options);
        var session = await agent.CreateSessionAsync();

        bool sawResult = false;
        var finalText = "";
        await foreach (var update in agent.RunStreamingAsync("600000", session))
        {
            foreach (var c in update.Contents)
                if (c is FunctionResultContent) sawResult = true;
            if (!string.IsNullOrEmpty(update.Text)) finalText += update.Text;
        }

        Assert.True(sawResult, "MAF 应执行 Echo 并回填 FunctionResultContent");
        Assert.Equal("fs结果:echo:600000", finalText);
    }

    [Fact]
    public async Task Fs_linear_brain_chains_calls_in_order()
    {
        // 两次 let! Call 顺序执行；mutable 字段跨挂起存活；第二次调用用第一次结果。
        Assert.True(FsRuleScriptCompiler.TryCompile(LinearChainFs, out var brain, out var error), error);
        var echo = AIFunctionFactory.Create((Func<string, string>)Echo);
        var client = new RuleChatClient(brain!);
        var options = new HarnessAgentOptions
        {
            Id = "fs-rule-chain",
            Name = "FsRuleChain",
            Description = "f# chained rule",
            DisableTodoProvider = true,
            ChatOptions = new ChatOptions { Instructions = "code", Tools = new List<AITool> { echo } },
        };
        var agent = client.AsHarnessAgent(options);
        var session = await agent.CreateSessionAsync();

        var finalText = "";
        int resultCount = 0;
        await foreach (var update in agent.RunStreamingAsync("hi", session))
        {
            foreach (var c in update.Contents)
                if (c is FunctionResultContent) resultCount++;
            if (!string.IsNullOrEmpty(update.Text)) finalText += update.Text;
        }

        Assert.Equal(2, resultCount);
        // captured = "hi_s0|echo:hi_s0"，second = echo:echo:hi_s0 → 终答 "hi_s0|echo:hi_s0||echo:echo:hi_s0"
        Assert.Equal("hi_s0|echo:hi_s0||echo:echo:hi_s0", finalText);
    }

    [Fact]
    public async Task Fs_stepwise_brain_drives_agent_loop()
    {
        // 每步式（IFsRuleBrain 经 FsRuleBrainAdapter）同过 MAF 循环：Call → 工具执行 → Reply 终答。
        Assert.True(FsRuleScriptCompiler.TryCompile(StepwiseRouterFs, out var brain, out var error), error);
        var echo = AIFunctionFactory.Create((Func<string, string>)Echo);
        var client = new RuleChatClient(brain!);
        var options = new HarnessAgentOptions
        {
            Id = "fs-rule-stepwise",
            Name = "FsRuleStepwise",
            Description = "f# stepwise rule",
            DisableTodoProvider = true,
            ChatOptions = new ChatOptions { Instructions = "code", Tools = new List<AITool> { echo } },
        };
        var agent = client.AsHarnessAgent(options);
        var session = await agent.CreateSessionAsync();

        var finalText = "";
        await foreach (var update in agent.RunStreamingAsync("abc", session))
        {
            if (!string.IsNullOrEmpty(update.Text)) finalText += update.Text;
        }

        Assert.Equal("fs回:echo:abc", finalText);
    }

    private static string Echo(string text) => $"echo:{text}";
}
