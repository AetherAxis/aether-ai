using System.Collections.Concurrent;
using System.Diagnostics;

namespace Aether.AI.Harness.Telemetry;

/// <summary>单条 trace 事件（tool call/result/approval/error/usage）。</summary>
public sealed record TraceEventEntry(string Name, string? Detail);

/// <summary>一次 agent.run 的 trace 记录。</summary>
public sealed record TraceEntry(
    string? AgentId, string? ConversationId, string? Mode,
    string Status, TimeSpan Duration, DateTimeOffset StartTime,
    IReadOnlyList<TraceEventEntry> Events)
{
    public string DurationText => Duration.TotalSeconds < 1 ? $"{Duration.TotalMilliseconds:F0}ms" : $"{Duration.TotalSeconds:F1}s";
    public int ToolCallCount => Events.Count(e => e.Name == AetherActivitySource.EventToolCall);
    public string StatusBadge => Status == "ok" ? "✅" : Status == "error" ? "❌" : "⏳";
    public bool IsError => Status != "ok";
    /// <summary>相对时间（"刚刚" / "5分钟前" / "2小时前"）。</summary>
    public string RelativeTime
    {
        get
        {
            var diff = DateTimeOffset.UtcNow - StartTime;
            return diff.TotalMinutes switch
            {
                < 1 => "刚刚",
                < 60 => $"{(int)diff.TotalMinutes}分钟前",
                < 1440 => $"{(int)diff.TotalHours}小时前",
                _ => StartTime.ToString("MM-dd HH:mm"),
            };
        }
    }
}

/// <summary>
/// 收集 <c>Aether.AI.Harness</c> ActivitySource 的 <c>agent.run</c> trace，存内存环形缓冲（最近 50 条）。
/// 供应用内 trace 查看页展示——桌面端无控制台时也能看到每轮 run 的耗时/工具/错误。
/// </summary>
public sealed class TraceCollector : IDisposable
{
    private readonly ConcurrentQueue<TraceEntry> _traces = new();
    private const int MaxTraces = 50;
    private readonly ActivityListener _listener;

    /// <summary>新 trace 完成时触发（UI 可订阅刷新）。</summary>
    public event Action<TraceEntry>? TraceCompleted;

    public TraceCollector()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AetherActivitySource.Name,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = OnActivityStopped,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    private void OnActivityStopped(Activity activity)
    {
        if (activity.OperationName != AetherActivitySource.ActivityAgentRun) return;

        var entry = new TraceEntry(
            AgentId: activity.GetTagItem(AetherActivitySource.TagAgentId)?.ToString(),
            ConversationId: activity.GetTagItem(AetherActivitySource.TagConversationId)?.ToString(),
            Mode: activity.GetTagItem(AetherActivitySource.TagMode)?.ToString(),
            Status: activity.GetTagItem(AetherActivitySource.TagStatus)?.ToString() ?? "?",
            Duration: activity.Duration,
            StartTime: activity.StartTimeUtc,
            Events: activity.Events.Select(e => new TraceEventEntry(
                e.Name,
                e.Tags.FirstOrDefault(t => t.Key is "name" or "message" or "summary" or "callId" or "requestId").Value?.ToString()
            )).ToList()
        );

        _traces.Enqueue(entry);
        while (_traces.Count > MaxTraces) _traces.TryDequeue(out _);
        TraceCompleted?.Invoke(entry);
    }

    public IReadOnlyList<TraceEntry> GetTraces() => _traces.Reverse().ToList(); // 最新在前

    public void Clear() { while (_traces.TryDequeue(out _)) { } }

    public void Dispose() => _listener.Dispose();
}
