namespace Aether.AI.Core.Agents;

/// <summary>
/// 运行关键节点信号（<see cref="RunProgress"/>）的消费者出口。信号源在 Harness 内
/// （<see cref="StreamHandlers.OnProgress"/>，见 <c>HarnessRuntime.RunStreamingCoreAsync</c>），
/// 凡经 Harness 跑的一轮 run——聊天 VM、工作流、Automation 定时任务执行器——都能拿到，
/// 不再依赖聊天 VM 在场。
/// <para>
/// 消费者示例：桌宠气泡（桌面）、未来任何悬浮形态、自动化运行的可观测性。
/// </para>
/// <para>
/// 粒度约定：只收关键节点（<see cref="RunProgressKind"/>），**不收逐字 token**——需要打字机效果的
/// UI 另走细粒度通道（如桌宠的 <c>IPetTokenStream</c>），避免只想看进度的消费者被逐字刷屏。
/// </para>
/// 与本文件同层（Core，框架无关、零依赖）：定义与实现分离，实现方（宿主/UI 层）自行注册。
/// </summary>
public interface IRunProgressSink
{
    /// <summary>接收一条关键节点信号。消费方负责 UI 线程 marshal + 各自的呈现翻译。</summary>
    void Post(RunProgress signal);
}

/// <summary>
/// 把一条关键节点信号广播给多个 <see cref="IRunProgressSink"/>（HarnessRuntime 的 DI 默认出口）。
/// 纯 BCL、零依赖，故可放 Core 供 Hosting 直接实例化。
/// <para>
/// 内置两类去重（都只影响进度频道，不影响 transcript/持久化）：
/// <list type="number">
///   <item><b>连续同节点折叠</b>：完全相同的 (Kind, ToolName) 连续重复只放行一次（防上游同一事件源
///     意外双发）。</item>
///   <item><b>轮询工具折叠</b>：<paramref name="pollingToolNames"/> 里的工具（check/wait 语义，agent 会
///     反复调用直到终态）每轮 run 只放行**一对** Start/Finish（第 2 次及以后的心跳被吃掉），否则进度频道
///     会被同名节点刷屏。工具名一换即视为新节点。</item>
/// </list>
/// 两条状态都在 <see cref="RunProgressKind.RunDone"/>/<see cref="RunProgressKind.RunError"/> 时重置，故
/// 下一轮 run 不受上一轮影响。
/// </para>
/// <para>线程安全：消费方可能在流的并发回调里 Post，故内部加锁。消费者抛异常不影响其它消费者或运行本身。</para>
/// </summary>
public sealed class RunProgressFanout : IRunProgressSink
{
    /// <summary>默认轮询型工具名单（与 <c>ChatWorkspaceViewModel.PollingToolNames</c> 同源语义：
    /// 这类工具「检查当前状态/等待完成」，会被反复调用。传空集合即关闭轮询折叠。）</summary>
    public static readonly IReadOnlyList<string> DefaultPollingToolNames =
        new[] { "PollSqlTaskStatus", "FetchSqlTaskStatus" };

    private readonly List<IRunProgressSink> _sinks = new();
    private readonly HashSet<string> _pollingToolNames;
    private readonly object _gate = new();

    // 本轮 run 内：(Kind, ToolName) → 上次放行的节点（连续同节点折叠）。
    private (RunProgressKind Kind, string Name)? _lastNode;
    // 本轮 run 内：轮询工具名 → 已放行的节点数（上限 2 = 一对 Start/Finish）。
    private readonly Dictionary<string, int> _pollingNodeCount = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="pollingToolNames">轮询型工具名单；null → <see cref="DefaultPollingToolNames"/>。</param>
    public RunProgressFanout(IEnumerable<string>? pollingToolNames = null)
        => _pollingToolNames = new HashSet<string>(
            pollingToolNames ?? DefaultPollingToolNames, StringComparer.OrdinalIgnoreCase);

    /// <summary>追加一个消费者。返回自身便于链式注册。</summary>
    public RunProgressFanout Add(IRunProgressSink sink)
    {
        if (sink is null) return this;
        lock (_gate) _sinks.Add(sink);
        return this;
    }

    /// <summary>移除一个消费者（消费方生命周期结束，如桌宠关闭）。</summary>
    public void Remove(IRunProgressSink sink)
    {
        lock (_gate) _sinks.Remove(sink);
    }

    /// <summary>当前消费者数（测试/诊断用）。</summary>
    public int Count { get { lock (_gate) return _sinks.Count; } }

    public void Post(RunProgress signal)
    {
        IRunProgressSink[] targets;
        lock (_gate)
        {
            if (signal.Kind is RunProgressKind.RunDone or RunProgressKind.RunError)
            {
                // 本轮收敛 → 重置去重状态（下一轮 run 重新计数）。终态**不**做连续折叠：
                // 一轮里可能先有非致命 ErrorContent（RunError）再因异常收敛出终态 RunError，
                // 两条 Text 不同、都要送达；折叠会吞掉后者。
                _lastNode = null;
                _pollingNodeCount.Clear();
            }
            else if (signal.ToolName is { } toolName
                     && signal.Kind is RunProgressKind.ToolStarted or RunProgressKind.ToolFinished)
            {
                if (_pollingToolNames.Contains(toolName))
                {
                    _pollingNodeCount.TryGetValue(toolName, out var n);
                    if (n >= 2) return; // 第 2 次及以后的轮询心跳：折叠
                    _pollingNodeCount[toolName] = n + 1;
                }
                var node = (signal.Kind, toolName);
                if (_lastNode == node) return; // 连续完全相同的节点：折叠
                _lastNode = node;
            }

            targets = _sinks.ToArray();
        }

        foreach (var sink in targets)
        {
            // 一个消费者抛异常不得影响其它消费者或运行本身。
            try { sink.Post(signal); }
            catch { /* 进度是旁路信号，失败静默 */ }
        }
    }
}
