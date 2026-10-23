using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// 代码规则大脑的抽象基类：作者用一个 async 方法写确定性流程，代替 LLM 与声明式 YAML <see cref="RuleProgramBrain"/>。
/// 镜像 Trading 策略脚本管线——笔记里写 C# 源码，Roslyn 即时编译成本类子类，注册成工厂（每次召唤 fresh instance，
/// 因代码大脑可有状态）。
/// <para>
/// 作者模型为<strong>协程式</strong>：override <see cref="RunAsync"/>，在其中 <c>await Call("工具", new {{ 参数=值 }})</c>
/// 调工具（挂起协程、把工具调用交给 MAF 执行、结果回填后恢复，await 返回工具结果字符串），<c>return "文本"</c>
/// 终答（终止 agent 循环）。状态靠作者自有字段在多次 <c>await</c> 间累积（实例在同一轮内复用）。
/// </para>
/// <para>
/// <strong>协程→拉式循环桥接</strong>：MAF 的 agent 循环是拉式的——<see cref="RuleChatClient"/> 每轮调一次
/// <see cref="DecideAsync"/> 取本步要流出的 <see cref="AIContent"/>。而作者写的 <see cref="RunAsync"/> 是 push 式协程。
/// 基类用自定义 awaiter（<see cref="CallAwaiter"/>，<see cref="CallAwaiter.IsCompleted"/> 恒 false）把两者缝合：
/// 首次调用（<see cref="Step"/>=0）启动 <see cref="RunAsync"/>，它跑到第一个 <c>await Call</c> 挂起（<c>OnCompleted</c>
/// 捕获续体），基类把该工具调用作为本步输出返回 MAF；MAF 执行工具回填结果后调 <c>DecideAsync</c> 再来一次，
/// 基类把结果喂回协程并恢复续体——协程继续跑到下一个挂起点或 <c>return</c>。<c>return</c> 的字符串即终答。
/// </para>
/// <para>
/// 对称于 <see cref="RuleProgramBrain"/>：同样实现 <see cref="IRuleBrain"/>，经 <see cref="RuleChatClient"/> 接入
/// MAF HarnessAgent，工具执行/回填/循环终止全归框架。区别仅在「决定下一步」用 C# 协程而非声明式规则程序。
/// </para>
/// </summary>
public abstract class CodeRuleBrain : IRuleBrain
{
    /// <summary>当前求值上下文（<see cref="DecideAsync"/> 调用前由基类设置）。保持 protected——原始管线数据。</summary>
    protected RuleContext Context { get; private set; } = null!;

    // 以下便利属性刻意 public（原 protected）：F# 不能在 task/lambda 闭包内访问 C# protected 成员
    //（C# 允许，F# 语言限制）——F# 线性大脑（FsCodeRuleBrain）的 task { let! ... } 里要用它们。
    // 只读视图，公开无副作用；C# 作者用法不变。

    /// <summary>本轮最新 user 消息文本（无则空串）。</summary>
    public string UserText => Context.LatestUser?.Text ?? string.Empty;

    /// <summary>本轮已调工具次数（最后一条 user 消息之后的 FunctionCallContent 计数）= 协程已恢复次数。</summary>
    public int Step => Context.ToolCallsThisTurn;

    /// <summary>历史里最新一条工具结果文本（无则空串）。协程里通常直接用 <c>await Call</c> 的返回值，此为兜底。</summary>
    public string LastResult => Context.LatestToolResult?.Result?.ToString() ?? string.Empty;

    /// <summary>本轮可用的工具（来自 ChatOptions.Tools）。</summary>
    public IReadOnlyList<AIFunction> AvailableTools => Context.AvailableTools;

    /// <summary>完整对话历史（含 system / 历史 turn / 本轮工具调用与结果）。</summary>
    public IReadOnlyList<ChatMessage> History => Context.History;

    /// <summary>
    /// 作者实现：写确定性流程。<c>await Call("工具", new {{ 参数=值 }})</c> 调工具并取结果（字符串）；
    /// <c>return "文本"</c> 终答（终止 agent 循环）。须保证最终 return，否则循环不结束（MAF 迭代上限兜底）。
    /// </summary>
    protected abstract Task<string> RunAsync();

    /// <summary>
    /// 产一个工具调用 awaitable：<paramref name="tool"/> 名 + <paramref name="args"/>（匿名对象/字典）。
    /// <c>await</c> 它挂起协程、把调用交给 MAF 执行，恢复后返回工具结果字符串。
    /// </summary>
    protected CallAwaitable Call(string tool, object? args = null)
    {
        if (string.IsNullOrEmpty(tool)) throw new ArgumentException("工具名不能为空", nameof(tool));
        return new CallAwaitable(this, tool, ToArgs(args));
    }

    /// <inheritdoc/>
    /// <remarks>协程驱动器：把拉式循环（MAF 每轮调一次）映射到 push 式 <see cref="RunAsync"/> 协程。</remarks>
    public ValueTask<IReadOnlyList<AIContent>> DecideAsync(RuleContext context, CancellationToken cancellationToken)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));

        if (context.ToolCallsThisTurn == 0)
        {
            // 新轮（本轮尚无工具调用）→（重）启协程，跑到第一个 await Call 或 return。
            ResetCoroutine();
            try { _runTask = RunAsync(); }
            catch (Exception ex) { return new(ErrorContent(ex)); }
        }
        else
        {
            // 续轮：把最新工具结果喂回协程并恢复续体。
            _pendingResult = context.LatestToolResult?.Result?.ToString() ?? string.Empty;
            var cont = _continuation;
            _continuation = null;
            if (cont is not null)
            {
                try { cont(); }
                catch (Exception ex) { return new(ErrorContent(ex)); }
            }
        }

        return new(ProduceContents());
    }

    // —— 协程驱动状态（跨 DecideAsync 调用，在一轮内复用；新轮 Step==0 时重置）——
    private Task<string>? _runTask;
    private Action? _continuation;
    private YieldKind _yieldKind = YieldKind.None;
    private string? _yieldTool;
    private Dictionary<string, object?>? _yieldArgs;
    private string? _pendingResult;

    private IReadOnlyList<AIContent> ProduceContents()
    {
        // 协程跑完 → 终答
        if (_runTask is { IsCompleted: true } task)
        {
            if (task.IsFaulted)
                return new[] { new TextContent("代码规则出错：" + (task.Exception?.GetBaseException()?.Message ?? "未知错误")) };
            return new[] { new TextContent(task.Result ?? string.Empty) };
        }

        // 挂起在 await Call → 产工具调用交 MAF 执行
        if (_yieldKind == YieldKind.Call)
        {
            var callId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            var fc = new FunctionCallContent(callId, _yieldTool!, _yieldArgs ?? new Dictionary<string, object?>());
            _yieldKind = YieldKind.None;
            return new[] { fc };
        }

        // 未完成且未挂起（不应发生）→ 空输出，让 MAF 终止本轮
        return Array.Empty<AIContent>();
    }

    private void ResetCoroutine()
    {
        _runTask = null;
        _continuation = null;
        _yieldKind = YieldKind.None;
        _yieldTool = null;
        _yieldArgs = null;
        _pendingResult = null;
    }

    private static IReadOnlyList<AIContent> ErrorContent(Exception ex)
        => new[] { new TextContent("代码规则出错：" + ex.Message) };

    /// <summary>awaiter 回调：记录要 yield 的工具调用 + 捕获协程续体，然后返回（协程挂起）。</summary>
    internal void YieldCall(string tool, Dictionary<string, object?> args, Action continuation)
    {
        _yieldKind = YieldKind.Call;
        _yieldTool = tool;
        _yieldArgs = args;
        _continuation = continuation;
    }

    /// <summary>awaiter 取结果：协程恢复时返回喂回的工具结果，并清空待用。</summary>
    internal string ConsumePendingResult()
    {
        var r = _pendingResult ?? string.Empty;
        _pendingResult = null;
        return r;
    }

    /// <summary>把匿名对象/字典/空转成 FunctionCallContent 接受的参数字典。</summary>
    private static Dictionary<string, object?> ToArgs(object? args)
    {
        if (args is null) return new Dictionary<string, object?>(StringComparer.Ordinal);
        if (args is IDictionary<string, object?> already) return new Dictionary<string, object?>(already, StringComparer.Ordinal);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in args.GetType().GetProperties())
        {
            result[p.Name] = p.GetValue(args);
        }
        return result;
    }

    private enum YieldKind { None, Call }
}

/// <summary>
/// <see cref="CodeRuleBrain.Call"/> 返回的 awaitable：携带工具名 + 参数，<see cref="GetAwaiter"/> 产
/// <see cref="CallAwaiter"/>。
/// </summary>
public readonly struct CallAwaitable
{
    private readonly CodeRuleBrain _brain;
    private readonly string _tool;
    private readonly Dictionary<string, object?> _args;

    internal CallAwaitable(CodeRuleBrain brain, string tool, Dictionary<string, object?> args)
    {
        _brain = brain;
        _tool = tool;
        _args = args;
    }

    public CallAwaiter GetAwaiter() => new(_brain, _tool, _args);
}

/// <summary>
/// 工具调用 awaiter：实现 <see cref="ICriticalNotifyCompletion"/>（继承 INotifyCompletion）。
/// <see cref="IsCompleted"/> 恒 false（总是挂起），<see cref="OnCompleted"/> 把续体交大脑 yield，
/// <see cref="GetResult"/> 在协程恢复时返回喂回的工具结果。
/// <para>实现 ICriticalNotifyCompletion（UnsafeOnCompleted = OnCompleted，无 ExecutionContext 流转差异）：
/// ① C# 编译器对实现它的 awaiter 走 AwaitUnsafeOnCompleted，语义与原 INotifyCompletion 路径一致；
/// ② F# task CE 只能 await 满足 FS-1097 GetAwaiter 模式的 awaiter，其 SRTP 约束要求
/// <c>:&gt; ICriticalNotifyCompletion</c>——补上它后 <c>FsCodeRuleBrain</c> 的 <c>task { let! r = Call(...) }</c>
/// 才能编译。这是 C# 基类对 F# 子类的唯一让步（3 行，零行为变化）。</para>
/// </summary>
public sealed class CallAwaiter : ICriticalNotifyCompletion
{
    private readonly CodeRuleBrain _brain;
    private readonly string _tool;
    private readonly Dictionary<string, object?> _args;

    internal CallAwaiter(CodeRuleBrain brain, string tool, Dictionary<string, object?> args)
    {
        _brain = brain;
        _tool = tool;
        _args = args;
    }

    public bool IsCompleted => false;

    public void OnCompleted(Action continuation) => _brain.YieldCall(_tool, _args, continuation);

    public void UnsafeOnCompleted(Action continuation) => _brain.YieldCall(_tool, _args, continuation);

    public string GetResult() => _brain.ConsumePendingResult();
}
