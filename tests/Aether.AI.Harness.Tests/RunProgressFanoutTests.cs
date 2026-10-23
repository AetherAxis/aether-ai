using Aether.AI.Core.Agents;

namespace Aether.AI.Harness.Tests;

/// <summary>
/// <see cref="RunProgressFanout"/> 去重/广播语义单测（纯 Core，无需 Harness）。
/// 去重只影响进度频道，不影响 transcript/持久化，故这些断言就是它的完整契约。
/// </summary>
public class RunProgressFanoutTests
{
    private sealed class RecordingSink : IRunProgressSink
    {
        private readonly List<RunProgress> _signals = new();
        public IReadOnlyList<RunProgress> Signals { get { lock (_signals) return _signals.ToList(); } }
        public void Post(RunProgress signal) { lock (_signals) _signals.Add(signal); }
    }

    private sealed class ThrowingSink : IRunProgressSink
    {
        public void Post(RunProgress signal) => throw new InvalidOperationException("consumer boom");
    }

    // 关掉轮询折叠，隔离测「连续同节点」折叠。
    private static RunProgressFanout NoPolling() => new(Array.Empty<string>());

    [Fact]
    public void Consecutive_identical_node_is_folded_once()
    {
        var sink = new RecordingSink();
        var fanout = NoPolling().Add(sink);

        fanout.Post(new RunProgress(RunProgressKind.ToolStarted, ToolName: "Echo"));
        fanout.Post(new RunProgress(RunProgressKind.ToolStarted, ToolName: "Echo")); // 连续同一个 → 折叠
        fanout.Post(new RunProgress(RunProgressKind.ToolStarted, ToolName: "Echo")); // 仍折叠

        Assert.Single(sink.Signals);
    }

    [Fact]
    public void Start_then_finish_of_same_tool_both_survive()
    {
        // 关键回归守卫：去重键是 (Kind, ToolName) 而非裸 ToolName——
        // 若只按工具名去重，正常的 Start→Finish 第二个节点会被吃掉，工具卡永远不显示完成。
        var sink = new RecordingSink();
        var fanout = NoPolling().Add(sink);

        fanout.Post(new RunProgress(RunProgressKind.ToolStarted, ToolName: "Echo"));
        fanout.Post(new RunProgress(RunProgressKind.ToolFinished, ToolName: "Echo"));

        Assert.Equal(2, sink.Signals.Count);
        Assert.Equal(RunProgressKind.ToolStarted, sink.Signals[0].Kind);
        Assert.Equal(RunProgressKind.ToolFinished, sink.Signals[1].Kind);
    }

    [Fact]
    public void Distinct_tools_are_never_folded()
    {
        var sink = new RecordingSink();
        var fanout = NoPolling().Add(sink);

        fanout.Post(new RunProgress(RunProgressKind.ToolStarted, ToolName: "A"));
        fanout.Post(new RunProgress(RunProgressKind.ToolStarted, ToolName: "B"));
        fanout.Post(new RunProgress(RunProgressKind.ToolStarted, ToolName: "A"));

        Assert.Equal(3, sink.Signals.Count);
    }

    [Fact]
    public void Polling_tool_emits_only_one_start_finish_pair_per_run()
    {
        var sink = new RecordingSink();
        var fanout = new RunProgressFanout().Add(sink); // 默认名单含 PollSqlTaskStatus

        // 第 1 次轮询：Start + Finish 放行
        fanout.Post(new RunProgress(RunProgressKind.ToolStarted, ToolName: "PollSqlTaskStatus"));
        fanout.Post(new RunProgress(RunProgressKind.ToolFinished, ToolName: "PollSqlTaskStatus"));
        // 第 2 次轮询心跳：全部折叠（否则进度频道被同名节点刷屏）
        fanout.Post(new RunProgress(RunProgressKind.ToolStarted, ToolName: "PollSqlTaskStatus"));
        fanout.Post(new RunProgress(RunProgressKind.ToolFinished, ToolName: "PollSqlTaskStatus"));
        // 第 3 次同上
        fanout.Post(new RunProgress(RunProgressKind.ToolStarted, ToolName: "PollSqlTaskStatus"));

        Assert.Equal(2, sink.Signals.Count);
    }

    [Fact]
    public void RunDone_resets_dedup_state_for_next_run()
    {
        var sink = new RecordingSink();
        var fanout = new RunProgressFanout().Add(sink);

        fanout.Post(new RunProgress(RunProgressKind.ToolStarted, ToolName: "PollSqlTaskStatus"));
        fanout.Post(new RunProgress(RunProgressKind.ToolFinished, ToolName: "PollSqlTaskStatus"));
        fanout.Post(new RunProgress(RunProgressKind.ToolStarted, ToolName: "PollSqlTaskStatus")); // 折叠
        fanout.Post(new RunProgress(RunProgressKind.RunDone));

        // 下一轮 run：同名工具重新计数 → 放行
        fanout.Post(new RunProgress(RunProgressKind.ToolStarted, ToolName: "PollSqlTaskStatus"));

        Assert.Equal(4, sink.Signals.Count);
        Assert.Equal(RunProgressKind.RunDone, sink.Signals[2].Kind);
        Assert.Equal(RunProgressKind.ToolStarted, sink.Signals[3].Kind);
    }

    [Fact]
    public void RunError_also_resets_dedup_state()
    {
        var sink = new RecordingSink();
        var fanout = new RunProgressFanout().Add(sink);

        fanout.Post(new RunProgress(RunProgressKind.ToolStarted, ToolName: "PollSqlTaskStatus"));
        fanout.Post(new RunProgress(RunProgressKind.ToolFinished, ToolName: "PollSqlTaskStatus"));
        fanout.Post(new RunProgress(RunProgressKind.RunError, Text: "boom"));
        fanout.Post(new RunProgress(RunProgressKind.ToolStarted, ToolName: "PollSqlTaskStatus"));

        Assert.Equal(4, sink.Signals.Count);
    }

    [Fact]
    public void One_consumer_throwing_does_not_starve_others()
    {
        // 进度是旁路信号：一个消费者的异常不得影响其它消费者，更不得冒泡进运行本身。
        var good = new RecordingSink();
        var fanout = new RunProgressFanout();
        fanout.Add(new ThrowingSink());
        fanout.Add(good);

        fanout.Post(new RunProgress(RunProgressKind.RunDone));

        Assert.Single(good.Signals);
    }

    [Fact]
    public void Removed_consumer_stops_receiving()
    {
        var sink = new RecordingSink();
        var fanout = new RunProgressFanout();
        fanout.Add(sink);
        fanout.Post(new RunProgress(RunProgressKind.Reasoning));
        Assert.Single(sink.Signals);

        fanout.Remove(sink);
        fanout.Post(new RunProgress(RunProgressKind.Reasoning, ToolName: null));

        Assert.Single(sink.Signals);
        Assert.Equal(0, fanout.Count);
    }

    [Fact]
    public void Add_null_and_remove_null_are_noops()
    {
        var fanout = new RunProgressFanout();
        fanout.Add(null!);
        fanout.Remove(null!);
        Assert.Equal(0, fanout.Count);
    }
}
