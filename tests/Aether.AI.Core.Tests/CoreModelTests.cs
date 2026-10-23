using Aether.AI.Core.Agents;
using Aether.AI.Core.Conversations.Runs;
using Aether.AI.Core.Messages;

namespace Aether.AI.Core.Tests;

/// <summary>
/// Core 层的框架无关模型。这些类型是 UI / 存储 / 适配器共同的语言，
/// 语义（工厂方法的角色、空内容判定、记录投影字段）一旦漂移，问题会出现在很远的地方。
/// </summary>
public class CoreModelTests
{
    [Fact]
    public void Message_factories_set_role_and_content()
    {
        Assert.Equal(MessageRole.System, Message.System("s").Role);
        Assert.Equal(MessageRole.User, Message.User("u").Role);
        Assert.Equal(MessageRole.Assistant, Message.Assistant("a").Role);

        var tool = Message.Tool("result", "ReadFile", "call-1");
        Assert.Equal(MessageRole.Tool, tool.Role);
        Assert.Equal("result", tool.Content);
        Assert.Equal("ReadFile", tool.ToolName);
        Assert.Equal("call-1", tool.ToolCallId);
    }

    [Fact]
    public void Only_tool_messages_carry_tool_metadata()
    {
        Assert.Null(Message.User("u").ToolName);
        Assert.Null(Message.User("u").ToolCallId);
        Assert.Null(Message.Tool("r", "T").ToolCallId);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("   \n\t ", true)]
    [InlineData("x", false)]
    public void IsEmpty_treats_whitespace_as_empty(string content, bool expected)
        => Assert.Equal(expected, Message.User(content).IsEmpty);

    [Fact]
    public void ToString_is_role_and_content_only()
        => Assert.Equal("[User] hi", Message.User("hi").ToString());

    [Fact]
    public void ToolStreamEvent_is_a_value_record()
    {
        var input = new Dictionary<string, object> { ["path"] = "/tmp/a" };
        var first = new ToolStreamEvent(ToolStreamEventKind.Call, "ReadFile", "call-1", input, null);
        var same = new ToolStreamEvent(ToolStreamEventKind.Call, "ReadFile", "call-1", input, null);
        var result = first with { Kind = ToolStreamEventKind.Result, Output = new Dictionary<string, object> { ["ok"] = true } };

        Assert.Equal(first, same);
        Assert.Equal(ToolStreamEventKind.Result, result.Kind);
        Assert.Null(first.Output);
        Assert.Same(input, first.Input);
    }

    [Fact]
    public void RunSummary_projects_run_fields_in_order()
    {
        var started = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.Zero);
        var summary = new RunSummary(
            Guid.NewGuid(), Guid.NewGuid(), "会话标题", "agent", "Agent 名",
            RunStatus.Completed, started, started.AddMinutes(2), "用户消息");

        Assert.Equal("会话标题", summary.ConversationTitle);
        Assert.Equal(RunStatus.Completed, summary.Status);
        Assert.Equal("用户消息", summary.UserMessage);
        Assert.Equal(started.AddMinutes(2), summary.FinishedAt);
        Assert.Equal(summary with { Status = RunStatus.Failed }, summary with { Status = RunStatus.Failed });
        Assert.NotEqual(summary.Status, (summary with { Status = RunStatus.Failed }).Status);
    }
}
