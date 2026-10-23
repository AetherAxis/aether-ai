using Aether.AI.Harness.Agents;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Tests;

/// <summary>
/// 验证 <see cref="SlidingWindowChatReducer"/> 截断时不切断 tool_use→tool_result 配对、
/// 且窗口首条为 user 角色——否则 GLM/Anthropic 兼容端点会报 messages 非法（如 GLM [1214]）。
/// </summary>
public class SlidingWindowChatReducerTests
{
    [Fact]
    public async Task Under_limit_returns_all()
    {
        var reducer = new SlidingWindowChatReducer(5);
        var msgs = new List<ChatMessage> { User("u1"), Assistant("a1"), User("u2") };

        var result = (await reducer.ReduceAsync(msgs)).ToList();

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task Truncate_keeps_recent_and_starts_at_user()
    {
        var reducer = new SlidingWindowChatReducer(3);
        var msgs = new List<ChatMessage>
        {
            User("u1"), Assistant("a1"),
            User("u2"), Assistant("a2"),
            User("u3"),
        };

        var result = (await reducer.ReduceAsync(msgs)).ToList();

        // 截断后首条必须是 user 文本
        Assert.Equal(ChatRole.User, result[0].Role);
        Assert.Contains(result, m => TextOf(m) == "u3");
        Assert.DoesNotContain(result, m => TextOf(m) == "u1");
    }

    [Fact]
    public async Task Truncate_does_not_sever_tool_pair()
    {
        // 三轮带工具调用的会话：user → assistant(tool_use) → user(tool_result) → assistant(answer)
        var reducer = new SlidingWindowChatReducer(4); // 小窗口强制截断
        var msgs = new List<ChatMessage>
        {
            User("u1"),                                  // 0 安全起点
            AssistantToolCall("call1", "foo"),           // 1
            UserToolResult("call1", "r1"),               // 2
            Assistant("a1"),                             // 3
            User("u2"),                                  // 4 安全起点
            AssistantToolCall("call2", "foo"),           // 5
            UserToolResult("call2", "r2"),               // 6
            Assistant("a2"),                             // 7
            User("u3"),                                  // 8 安全起点（当前）
        };

        var result = (await reducer.ReduceAsync(msgs)).ToList();

        // 1) 首条为 user 文本（不含 tool 结果）
        Assert.Equal(ChatRole.User, result[0].Role);
        Assert.DoesNotContain(result[0].Contents, c => c is FunctionResultContent);

        // 2) 窗口内每个 tool_result 都有其 tool_use（配对不被切断）
        var callIdsInResult = result
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Select(c => c.CallId)
            .ToHashSet();
        Assert.NotEmpty(callIdsInResult); // 确实保留了工具调用
        foreach (var r in result.SelectMany(m => m.Contents.OfType<FunctionResultContent>()))
            Assert.Contains(r.CallId, callIdsInResult);
    }

    [Fact]
    public async Task Tool_heavy_single_turn_does_not_truncate_when_no_safe_start()
    {
        // 单轮大量 tool 往返：唯一的 user 文本在最前面，超出回退范围 → 不截断（避免发非法序列）
        var reducer = new SlidingWindowChatReducer(3);
        var msgs = new List<ChatMessage>
        {
            User("u1"),                                  // 0（唯一的 user 文本）
            AssistantToolCall("c1", "foo"), UserToolResult("c1", "r1"),
            AssistantToolCall("c2", "foo"), UserToolResult("c2", "r2"),
            AssistantToolCall("c3", "foo"), UserToolResult("c3", "r3"),
            Assistant("done"),                          // 7
        };

        var result = (await reducer.ReduceAsync(msgs)).ToList();

        // 不截断：全部保留（首条仍是原始 user 文本，配对完整）
        Assert.Equal(msgs.Count, result.Count);
        Assert.Equal(ChatRole.User, result[0].Role);
    }

    // —— 构造辅助 ——

    private static ChatMessage User(string text)
    {
        var m = new ChatMessage { Role = ChatRole.User };
        m.Contents.Add(new TextContent(text));
        return m;
    }

    private static ChatMessage Assistant(string text)
    {
        var m = new ChatMessage { Role = ChatRole.Assistant };
        m.Contents.Add(new TextContent(text));
        return m;
    }

    private static ChatMessage AssistantToolCall(string callId, string name)
    {
        var m = new ChatMessage { Role = ChatRole.Assistant };
        m.Contents.Add(new FunctionCallContent(callId, name, new Dictionary<string, object?>()));
        return m;
    }

    private static ChatMessage UserToolResult(string callId, string result)
    {
        var m = new ChatMessage { Role = ChatRole.User };
        m.Contents.Add(new FunctionResultContent(callId, result));
        return m;
    }

    private static string? TextOf(ChatMessage m)
        => m.Contents.OfType<TextContent>().FirstOrDefault()?.Text;
}
