using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Aether.AI.Brains.Abstractions;
using Microsoft.Extensions.AI;

namespace Aether.AI.Brains.Code;

/// <summary>
/// Base class for deterministic code brains. Authors implement one asynchronous flow and use
/// <see cref="Call"/> to yield tool calls to the Harness execution loop.
/// </summary>
public abstract class CodeRuleBrain : IBrain
{
    private readonly object _gate = new();
    private Channel<CoroutineSignal>? _signals;
    private CancellationTokenSource? _runCancellation;
    private CancellationTokenRegistration _externalCancellationRegistration;
    private Task? _runTask;
    private Action? _callContinuation;
    private string? _pendingResult;
    private Exception? _pendingCallException;

    protected BrainContext Context { get; private set; } = null!;

    public string UserText => Context.LatestUser?.Text ?? string.Empty;
    public int Step => Context.ToolCallsThisTurn;
    public string LastResult => Context.LatestToolResult?.Result?.ToString() ?? string.Empty;
    public IReadOnlyList<AIFunction> AvailableTools => Context.AvailableTools;
    public IReadOnlyList<ChatMessage> History => Context.History;

    /// <summary>
    /// Cancellation token for ordinary asynchronous work inside <see cref="RunAsync"/>. Potentially
    /// unbounded operations must observe this token so session eviction and request cancellation can unwind the run.
    /// </summary>
    public CancellationToken RunCancellationToken => _runCancellation?.Token ?? CancellationToken.None;

    protected abstract Task<string> RunAsync();

    protected CallAwaitable Call(string tool, object? args = null)
    {
        if (string.IsNullOrEmpty(tool)) throw new ArgumentException("工具名不能为空", nameof(tool));
        return new CallAwaitable(this, tool, ToArgs(args));
    }

    public async ValueTask<IReadOnlyList<AIContent>> DecideAsync(
        BrainContext context, CancellationToken cancellationToken)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));

        if (context.ToolCallsThisTurn == 0)
        {
            StartRun(cancellationToken);
        }
        else
        {
            ResumeCall(context.LatestToolResult?.Result?.ToString() ?? string.Empty);
        }

        var signal = await ReadSignalAsync().ConfigureAwait(false);
        switch (signal)
        {
            case CallSignal call:
                return new AIContent[]
                {
                    new FunctionCallContent(
                        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                        call.Tool,
                        call.Arguments),
                };
            case CompletedSignal completed:
                CompleteRun();
                return new AIContent[] { new TextContent(completed.Text) };
            case FaultedSignal faulted:
                CompleteRun();
                return ErrorContent(faulted.Exception);
            case CanceledSignal canceled:
                CompleteRun();
                throw canceled.Exception;
            default:
                throw new InvalidOperationException("未知代码大脑协程信号。");
        }
    }

    private void StartRun(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_runTask is { IsCompleted: false })
                throw new InvalidOperationException("代码大脑实例已有运行中的协程。");

            DisposeRunState();
            _signals = Channel.CreateUnbounded<CoroutineSignal>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
            _runCancellation = new CancellationTokenSource();
            _externalCancellationRegistration = cancellationToken.Register(
                static state => ((CodeRuleBrain)state!).CancelRun(), this);
            _runTask = DriveRunAsync();
        }

        if (cancellationToken.IsCancellationRequested) CancelRun();
    }

    private async Task DriveRunAsync()
    {
        try
        {
            var result = await RunAsync().ConfigureAwait(false);
            Publish(new CompletedSignal(result ?? string.Empty));
        }
        catch (OperationCanceledException ex) when (RunCancellationToken.IsCancellationRequested)
        {
            Publish(new CanceledSignal(ex));
        }
        catch (Exception ex)
        {
            Publish(new FaultedSignal(ex));
        }
    }

    private async ValueTask<CoroutineSignal> ReadSignalAsync()
    {
        var signals = _signals ?? throw new InvalidOperationException("代码大脑协程尚未启动。");
        return await signals.Reader.ReadAsync().ConfigureAwait(false);
    }

    private void ResumeCall(string result)
    {
        Action continuation;
        lock (_gate)
        {
            continuation = _callContinuation
                ?? throw new InvalidOperationException("代码大脑没有等待中的工具调用。");
            _callContinuation = null;
            _pendingResult = result;
            _pendingCallException = null;
        }
        continuation();
    }

    internal void YieldCall(string tool, Dictionary<string, object?> args, Action continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        var resumeCanceled = false;
        lock (_gate)
        {
            if (_callContinuation is not null)
                throw new InvalidOperationException("代码大脑同时挂起了多个工具调用。");

            if (_runCancellation?.IsCancellationRequested == true)
            {
                _pendingCallException = new OperationCanceledException(_runCancellation.Token);
                resumeCanceled = true;
            }
            else
            {
                _callContinuation = continuation;
                Publish(new CallSignal(tool, args));
            }
        }

        if (resumeCanceled)
            ThreadPool.UnsafeQueueUserWorkItem(static state => ((Action)state!).Invoke(), continuation);
    }

    internal string ConsumePendingResult()
    {
        lock (_gate)
        {
            if (_pendingCallException is { } exception)
            {
                _pendingCallException = null;
                throw exception;
            }

            var result = _pendingResult ?? string.Empty;
            _pendingResult = null;
            return result;
        }
    }

    private void CancelRun()
    {
        Action? continuation;
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            cancellation = _runCancellation;
            if (cancellation is null || cancellation.IsCancellationRequested) return;

            continuation = _callContinuation;
            _callContinuation = null;
            if (continuation is not null)
                _pendingCallException = new OperationCanceledException(cancellation.Token);
        }

        cancellation.Cancel();
        continuation?.Invoke();
    }

    private void Publish(CoroutineSignal signal)
    {
        var signals = _signals ?? throw new InvalidOperationException("代码大脑协程尚未启动。");
        if (!signals.Writer.TryWrite(signal))
            throw new InvalidOperationException("代码大脑协程信号通道已关闭。");
    }

    private void CompleteRun()
    {
        lock (_gate) DisposeRunState();
    }

    private void DisposeRunState()
    {
        _externalCancellationRegistration.Dispose();
        _runCancellation?.Dispose();
        _signals = null;
        _runCancellation = null;
        _runTask = null;
        _callContinuation = null;
        _pendingResult = null;
        _pendingCallException = null;
    }

    private static IReadOnlyList<AIContent> ErrorContent(Exception ex)
        => new AIContent[] { new TextContent("代码规则出错：" + ex.Message) };

    private static Dictionary<string, object?> ToArgs(object? args)
    {
        if (args is null) return new Dictionary<string, object?>(StringComparer.Ordinal);
        if (args is IDictionary<string, object?> already)
            return new Dictionary<string, object?>(already, StringComparer.Ordinal);

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in args.GetType().GetProperties())
            result[property.Name] = property.GetValue(args);
        return result;
    }

    private abstract record CoroutineSignal;
    private sealed record CallSignal(string Tool, Dictionary<string, object?> Arguments) : CoroutineSignal;
    private sealed record CompletedSignal(string Text) : CoroutineSignal;
    private sealed record FaultedSignal(Exception Exception) : CoroutineSignal;
    private sealed record CanceledSignal(OperationCanceledException Exception) : CoroutineSignal;
}

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
