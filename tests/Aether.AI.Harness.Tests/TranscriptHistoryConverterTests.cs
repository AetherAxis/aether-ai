using Aether.AI.Core.Agents;
using Aether.AI.Core.Conversations;
using Aether.AI.Core.Conversations.Runs;
using Aether.AI.Harness.Agents;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Tests;

/// <summary>
/// 验证 <see cref="EventHistoryRebuilder"/> 从 typed 事件流重建 LLM 历史——结构不变量由构造保证：
/// call↔result 配对（CallId 一致）、role 交替、无孤儿、Interrupted 尾部孤儿 call 被丢、空 result 占位。
/// 取代旧的 <c>TranscriptHistoryConverter.Convert</c>+<c>Sanitize</c> restore saga 守卫。
/// </summary>
public class EventHistoryRebuilderTests
{
    [Fact]
    public void User_and_assistant_round_trip()
    {
        var run = Run(1, "你好");
        var events = new List<AgentEvent>
        {
            Ev(run.Id, 1, AgentEventType.TextChunk, new AgentEventPayload.TextChunk("你好！有什么可以帮你？")),
        };

        var result = EventHistoryRebuilder.RebuildLlmHistory(new[] { run }, ByRun(events));

        Assert.Equal(2, result.Count);
        Assert.Equal(ChatRole.User, result[0].Role);
        Assert.Equal("你好", result[0].Text);
        Assert.Equal(ChatRole.Assistant, result[1].Role);
        Assert.Equal("你好！有什么可以帮你？", result[1].Text);
    }

    [Fact]
    public void Reasoning_plan_approval_are_skipped()
    {
        var run = Run(1, "查天气");
        var events = new List<AgentEvent>
        {
            Ev(run.Id, 1, AgentEventType.ReasoningChunk, new AgentEventPayload.ReasoningChunk("让我想想")),
            Ev(run.Id, 2, AgentEventType.PlanUpdated, new AgentEventPayload.PlanUpdated(Array.Empty<TodoItemInfo>())),
            Ev(run.Id, 3, AgentEventType.ApprovalRequested, new AgentEventPayload.ApprovalRequested("req-1", "call-1", "GetWeather", null)),
            Ev(run.Id, 4, AgentEventType.TextChunk, new AgentEventPayload.TextChunk("北京晴")),
        };

        var result = EventHistoryRebuilder.RebuildLlmHistory(new[] { run }, ByRun(events));

        // 只剩 user + answer，reasoning/plan/approval 跳过
        Assert.Equal(2, result.Count);
        Assert.Equal(ChatRole.User, result[0].Role);
        Assert.Equal(ChatRole.Assistant, result[1].Role);
        Assert.Empty(result.SelectMany(m => m.Contents.OfType<FunctionCallContent>()));
    }

    [Fact]
    public void Tool_call_and_result_are_paired_with_matching_callid()
    {
        var run = Run(1, "查天气");
        var events = new List<AgentEvent>
        {
            Ev(run.Id, 1, AgentEventType.ToolCall,
                new AgentEventPayload.ToolCall("call-1", "GetWeather", new Dictionary<string, object> { ["city"] = "北京" })),
            Ev(run.Id, 2, AgentEventType.ToolResult,
                new AgentEventPayload.ToolResult("call-1", "GetWeather", new Dictionary<string, object> { ["result"] = "晴" })),
            Ev(run.Id, 3, AgentEventType.TextChunk, new AgentEventPayload.TextChunk("北京晴")),
        };

        var result = EventHistoryRebuilder.RebuildLlmHistory(new[] { run }, ByRun(events));

        // user → assistant(FunctionCall) → user(FunctionResult) → assistant(answer) = 4
        Assert.Equal(4, result.Count);
        Assert.Equal(ChatRole.User, result[0].Role);

        // tool_use: assistant + FunctionCallContent
        Assert.Equal(ChatRole.Assistant, result[1].Role);
        var call = result[1].Contents.OfType<FunctionCallContent>().Single();
        Assert.Equal("call-1", call.CallId);
        Assert.Equal("GetWeather", call.Name);

        // tool_result: user + FunctionResultContent（callId 匹配、相邻）
        Assert.Equal(ChatRole.User, result[2].Role);
        var res = result[2].Contents.OfType<FunctionResultContent>().Single();
        Assert.Equal("call-1", res.CallId);

        // answer
        Assert.Equal(ChatRole.Assistant, result[3].Role);
        Assert.Equal("北京晴", result[3].Text);
    }

    [Fact]
    public void Multiple_tools_all_paired_no_orphans()
    {
        var run = Run(1, "do two things");
        var events = new List<AgentEvent>
        {
            Ev(run.Id, 1, AgentEventType.ToolCall, new AgentEventPayload.ToolCall("call-a", "ToolA", new Dictionary<string, object> { ["x"] = 1 })),
            Ev(run.Id, 2, AgentEventType.ToolResult, new AgentEventPayload.ToolResult("call-a", "ToolA", new Dictionary<string, object> { ["r"] = 1 })),
            Ev(run.Id, 3, AgentEventType.ToolCall, new AgentEventPayload.ToolCall("call-b", "ToolB", new Dictionary<string, object> { ["y"] = 2 })),
            Ev(run.Id, 4, AgentEventType.ToolResult, new AgentEventPayload.ToolResult("call-b", "ToolB", new Dictionary<string, object> { ["r"] = 2 })),
            Ev(run.Id, 5, AgentEventType.TextChunk, new AgentEventPayload.TextChunk("done")),
        };

        var result = EventHistoryRebuilder.RebuildLlmHistory(new[] { run }, ByRun(events));

        // user → callA → resultA → callB → resultB → answer = 6
        Assert.Equal(6, result.Count);
        var calls = result.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).ToList();
        var results = result.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal(2, results.Count);
        // 每个 call 都有对应的 result（callId 匹配）
        foreach (var c in calls)
            Assert.Contains(results, r => r.CallId == c.CallId);
        // 每个 result 都有对应的 call（无孤儿）
        foreach (var r in results)
            Assert.Contains(calls, c => c.CallId == r.CallId);
    }

    [Fact]
    public void Interrupted_trailing_orphan_call_is_dropped()
    {
        // Interrupted run：发了 ToolCall 但无 ToolResult、无最终回答 → 孤儿 call 被丢。
        // 用户消息保留（run.UserMessage 总是加入），仅 call 被删。
        var run = Run(1, "查天气", RunStatus.Interrupted);
        var events = new List<AgentEvent>
        {
            Ev(run.Id, 1, AgentEventType.ToolCall, new AgentEventPayload.ToolCall("call-1", "GetWeather", null)),
            // 无 ToolResult、无 TextChunk
        };

        var result = EventHistoryRebuilder.RebuildLlmHistory(new[] { run }, ByRun(events));

        // 只剩 user 消息，孤儿 call 已删
        Assert.Equal(ChatRole.User, result[0].Role);
        Assert.Empty(result.SelectMany(m => m.Contents.OfType<FunctionCallContent>()));
    }

    [Fact]
    public void Interrupted_run_after_complete_run_keeps_complete_run()
    {
        // 有完整上一轮答案 + 不完整本轮（tool 无答案）→ 保留完整轮，本轮孤儿 call 被丢、user 保留。
        var run1 = Run(1, "q1");
        var run1Events = new List<AgentEvent>
        {
            Ev(run1.Id, 1, AgentEventType.TextChunk, new AgentEventPayload.TextChunk("a1")),
        };
        var run2 = Run(2, "q2", RunStatus.Interrupted);
        var run2Events = new List<AgentEvent>
        {
            Ev(run2.Id, 1, AgentEventType.ToolCall, new AgentEventPayload.ToolCall("call-1", "GetWeather", null)),
        };

        var result = EventHistoryRebuilder.RebuildLlmHistory(
            new[] { run1, run2 },
            ByRun(run1Events, run2Events));

        // q1 → a1 → q2（run2 孤儿 call 被丢，但 user 消息保留）
        Assert.Equal(3, result.Count);
        Assert.Equal(ChatRole.User, result[0].Role);
        Assert.Equal("q1", result[0].Text);
        Assert.Equal(ChatRole.Assistant, result[1].Role);
        Assert.Equal("a1", result[1].Text);
        Assert.Equal(ChatRole.User, result[2].Role);
        Assert.Equal("q2", result[2].Text);
        Assert.Empty(result.SelectMany(m => m.Contents.OfType<FunctionCallContent>()));
    }

    [Fact]
    public void Empty_tool_output_gets_placeholder()
    {
        var run = Run(1, "q");
        var events = new List<AgentEvent>
        {
            Ev(run.Id, 1, AgentEventType.ToolCall, new AgentEventPayload.ToolCall("call-1", "GetWeather", null)),
            // output=null → 占位 (无返回)，防端点拒空 tool_result
            Ev(run.Id, 2, AgentEventType.ToolResult, new AgentEventPayload.ToolResult("call-1", "GetWeather", null)),
            Ev(run.Id, 3, AgentEventType.TextChunk, new AgentEventPayload.TextChunk("done")),
        };

        var result = EventHistoryRebuilder.RebuildLlmHistory(new[] { run }, ByRun(events));

        var res = result.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Single();
        Assert.NotNull(res.Result);
        Assert.Equal("(无返回)", res.Result);
    }

    [Fact]
    public void Text_before_tool_is_merged_into_assistant_message()
    {
        // TextChunk 紧随 ToolCall → 合并进同一 assistant 消息（TextContent + FunctionCallContent）。
        var run = Run(1, "查天气");
        var events = new List<AgentEvent>
        {
            Ev(run.Id, 1, AgentEventType.TextChunk, new AgentEventPayload.TextChunk("让我查一下")),
            Ev(run.Id, 2, AgentEventType.ToolCall, new AgentEventPayload.ToolCall("call-1", "GetWeather", null)),
            Ev(run.Id, 3, AgentEventType.ToolResult, new AgentEventPayload.ToolResult("call-1", "GetWeather", new Dictionary<string, object> { ["r"] = 1 })),
            Ev(run.Id, 4, AgentEventType.TextChunk, new AgentEventPayload.TextChunk("北京晴")),
        };

        var result = EventHistoryRebuilder.RebuildLlmHistory(new[] { run }, ByRun(events));

        // user → assistant(text+call) → user(result) → assistant(answer) = 4
        Assert.Equal(4, result.Count);
        Assert.Equal(ChatRole.Assistant, result[1].Role);
        // 同一 assistant 消息含 TextContent + FunctionCallContent
        Assert.Single(result[1].Contents.OfType<TextContent>());
        Assert.Single(result[1].Contents.OfType<FunctionCallContent>());
        Assert.Equal("让我查一下", result[1].Contents.OfType<TextContent>().Single().Text);
    }

    [Fact]
    public void Multiple_runs_preserve_order()
    {
        var run1 = Run(1, "q1");
        var run1Events = new List<AgentEvent> { Ev(run1.Id, 1, AgentEventType.TextChunk, new AgentEventPayload.TextChunk("a1")) };
        var run2 = Run(2, "q2");
        var run2Events = new List<AgentEvent> { Ev(run2.Id, 1, AgentEventType.TextChunk, new AgentEventPayload.TextChunk("a2")) };

        var result = EventHistoryRebuilder.RebuildLlmHistory(
            new[] { run1, run2 },
            ByRun(run1Events, run2Events));

        Assert.Equal(4, result.Count);
        Assert.Equal(ChatRole.Assistant, result[^1].Role);
        Assert.Equal("a2", result[^1].Text);
    }

    [Fact]
    public void Roles_alternate_user_assistant()
    {
        // 重建后历史必须角色交替（user/assistant），否则 Anthropic 兼容端点 400。
        var run = Run(1, "查天气");
        var events = new List<AgentEvent>
        {
            Ev(run.Id, 1, AgentEventType.ToolCall, new AgentEventPayload.ToolCall("call-1", "GetWeather", null)),
            Ev(run.Id, 2, AgentEventType.ToolResult, new AgentEventPayload.ToolResult("call-1", "GetWeather", new Dictionary<string, object> { ["r"] = 1 })),
            Ev(run.Id, 3, AgentEventType.TextChunk, new AgentEventPayload.TextChunk("北京晴")),
        };

        var result = EventHistoryRebuilder.RebuildLlmHistory(new[] { run }, ByRun(events));

        // user → assistant(call) → user(result) → assistant(answer)：严格交替
        Assert.Equal(ChatRole.User, result[0].Role);
        Assert.Equal(ChatRole.Assistant, result[1].Role);
        Assert.Equal(ChatRole.User, result[2].Role);
        Assert.Equal(ChatRole.Assistant, result[3].Role);
    }

    [Fact]
    public void Rebuild_is_idempotent()
    {
        var run = Run(1, "q");
        var events = new List<AgentEvent>
        {
            Ev(run.Id, 1, AgentEventType.ToolCall, new AgentEventPayload.ToolCall("call-1", "GetWeather", null)),
            Ev(run.Id, 2, AgentEventType.ToolResult, new AgentEventPayload.ToolResult("call-1", "GetWeather", new Dictionary<string, object> { ["r"] = 1 })),
            Ev(run.Id, 3, AgentEventType.TextChunk, new AgentEventPayload.TextChunk("done")),
        };
        var runs = new[] { run };
        var map = ByRun(events);

        var once = EventHistoryRebuilder.RebuildLlmHistory(runs, map);
        var twice = EventHistoryRebuilder.RebuildLlmHistory(runs, map);

        Assert.Equal(once.Count, twice.Count);
        // 重建不修改输入事件，二次结果与一次一致
        Assert.Equal(once.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).Count(),
                     twice.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).Count());
    }

    // --- helpers ---

    private static AgentRun Run(int seq, string userMessage, RunStatus status = RunStatus.Completed)
        => new()
        {
            Id = Guid.NewGuid(),
            ConversationId = Guid.NewGuid(),
            Seq = seq,
            Status = status,
            AgentId = "agent-1",
            AgentName = "TestAgent",
            UserMessage = userMessage,
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow,
        };

    private static AgentEvent Ev(Guid runId, int seq, AgentEventType type, object? payload)
        => new()
        {
            RunId = runId,
            Seq = seq,
            EventType = type,
            Payload = AgentEventPayload.Serialize(payload),
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private static IReadOnlyDictionary<Guid, IReadOnlyList<AgentEvent>> ByRun(params List<AgentEvent>[] groups)
    {
        var dict = new Dictionary<Guid, IReadOnlyList<AgentEvent>>();
        foreach (var g in groups)
            if (g.Count > 0)
                dict[g[0].RunId] = g;
        return dict;
    }
}

/// <summary>
/// <see cref="TranscriptHistoryConverter.SanitizeProviderHistory"/> 专用测试——活路径
/// <c>RestoringChatHistoryProvider.ProvideChatHistoryAsync</c> 用它净化历史。
/// 核心回归守卫：活路径 FICC 循环里，最近一轮的 tool_call 在 Provide 时尚无 result
/// （result 下一轮 Store 才落地），<see cref="TranscriptHistoryConverter.SanitizeLive"/> 会把它当孤儿丢弃
/// → 下一轮请求变「有 result 无 call」→ GLM 重调同一工具（每个工具执行两遍）。SanitizeProviderHistory 必须保留它。
/// </summary>
public class TranscriptHistoryProviderSanitizeTests
{
    [Fact]
    public void InProgress_tool_call_without_result_is_preserved()
    {
        // 活路径：assistant 刚发了 tool_call，result 尚未落地（FICC 即将执行/已执行但下一轮才 Store）。
        var assistant = new ChatMessage { Role = ChatRole.Assistant };
        assistant.Contents.Add(new TextContent("我来查询"));
        assistant.Contents.Add(new FunctionCallContent("call-1", "QuerySecurity",
            new Dictionary<string, object?> { ["keyword"] = "大全能源" }));
        var history = new List<ChatMessage> { new(ChatRole.User, "查大全能源"), assistant };

        var result = TranscriptHistoryConverter.SanitizeProviderHistory(history);

        // tool_call 必须保留——否则下一轮请求变孤儿 result → 模型重调工具（重复执行）。
        var call = result.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).Single();
        Assert.Equal("call-1", call.CallId);
        Assert.Equal("QuerySecurity", call.Name);
    }

    [Fact]
    public void SanitizeLive_drops_in_progress_call_but_provider_sanitize_keeps_it()
    {
        // 对照：SanitizeLive 把「无 result 的 call」当孤儿丢弃（工具重复执行的根因）；
        // SanitizeProviderHistory 保留。这是 double-tool-execution bug 的核心回归守卫。
        var msg = new ChatMessage { Role = ChatRole.Assistant };
        msg.Contents.Add(new FunctionCallContent("call-1", "QuerySecurity", null));

        Assert.Empty(TranscriptHistoryConverter.SanitizeLive(new[] { msg })
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>()));
        Assert.Single(TranscriptHistoryConverter.SanitizeProviderHistory(new[] { msg })
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>()));
    }

    [Fact]
    public void Orphan_tool_result_without_call_is_still_dropped()
    {
        // 孤儿 tool_result（无前置 call）仍是结构非法（OpenAI 兼容端点会 400）→ 丢弃。
        var msg = new ChatMessage { Role = ChatRole.Tool };
        msg.Contents.Add(new FunctionResultContent("call-1", "result"));

        var result = TranscriptHistoryConverter.SanitizeProviderHistory(new[] { msg });

        Assert.Empty(result.SelectMany(m => m.Contents.OfType<FunctionResultContent>()));
    }

    [Fact]
    public void Paired_call_and_result_pass_through_unchanged()
    {
        var call = new ChatMessage { Role = ChatRole.Assistant };
        call.Contents.Add(new FunctionCallContent("call-1", "Tool", null));
        var res = new ChatMessage { Role = ChatRole.Tool };
        res.Contents.Add(new FunctionResultContent("call-1", "r"));

        var result = TranscriptHistoryConverter.SanitizeProviderHistory(new[] { call, res });

        Assert.Single(result.SelectMany(m => m.Contents.OfType<FunctionCallContent>()));
        Assert.Single(result.SelectMany(m => m.Contents.OfType<FunctionResultContent>()));
    }

    [Fact]
    public void Provider_sanitize_is_idempotent()
    {
        var assistant = new ChatMessage { Role = ChatRole.Assistant };
        assistant.Contents.Add(new FunctionCallContent("call-1", "Tool", null));
        var history = new List<ChatMessage> { new(ChatRole.User, "q"), assistant };

        var once = TranscriptHistoryConverter.SanitizeProviderHistory(history);
        var twice = TranscriptHistoryConverter.SanitizeProviderHistory(once);

        Assert.Equal(once.Count, twice.Count);
        Assert.Single(twice.SelectMany(m => m.Contents.OfType<FunctionCallContent>()));
    }
}
