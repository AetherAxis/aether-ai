using Aether.AI.Core.Conversations;
using Aether.AI.Core.Conversations.Runs;
using Aether.AI.Memory.Stores;

namespace Aether.AI.Memory.Tests;

/// <summary>
/// <see cref="InMemoryConversationStore"/> 的 NULL-userId 容忍语义回归守卫：
/// 桌面单用户的 <c>Conversation.UserId</c> 是 null（SQLite 落 NULL），查询传 ""——
/// 必须与 <c>SqliteConversationStore</c> 的 <c>@uid IS NULL OR user_id IS NULL OR user_id=@uid</c> 同语义。
/// 修复前 GetSummariesAsync/FindByUserAsync/GetRunsInRangeAsync 用窄匹配
/// （<c>UserId == ""</c> 不匹配 null），store 兜底切换时历史列表/记忆/活动热力图恒空。
/// </summary>
public class InMemoryConversationStoreTests
{
    [Fact]
    public async Task Null_user_id_conversations_are_visible_to_empty_string_queries()
    {
        var store = new InMemoryConversationStore();
        var desktop = new Conversation
        {
            ConversationId = Guid.NewGuid(),
            UserId = null, // 桌面单用户语义
            AgentId = "agent-1",
            AgentName = "Tester",
            Title = "桌面会话",
        };
        var someoneElses = new Conversation
        {
            ConversationId = Guid.NewGuid(),
            UserId = "user-42",
            AgentId = "agent-1",
            AgentName = "Tester",
            Title = "他人会话",
        };
        await store.SaveConversationAsync(desktop);
        await store.SaveConversationAsync(someoneElses);
        await store.AppendRunAsync(new AgentRun
        {
            ConversationId = desktop.ConversationId,
            Seq = 1,
            Status = RunStatus.Completed,
            AgentId = desktop.AgentId,
            AgentName = desktop.AgentName,
            UserMessage = "hi",
        }, Array.Empty<AgentEvent>());

        // 桌面查询传 ""（ChatWorkspaceViewModel/ActivityViewModel 的真实调用形态）。
        var summaries = await store.GetSummariesAsync("");
        Assert.Equal(desktop.ConversationId, Assert.Single(summaries).ConversationId);

        var byUser = await store.FindByUserAsync("");
        Assert.Equal(desktop.ConversationId, Assert.Single(byUser).ConversationId);

        var runs = await store.GetRunsInRangeAsync("", DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        Assert.Equal(desktop.ConversationId, Assert.Single(runs).ConversationId);
    }

    [Fact]
    public async Task Non_null_user_id_filters_by_exact_match()
    {
        var store = new InMemoryConversationStore();
        var mine = new Conversation { ConversationId = Guid.NewGuid(), UserId = "user-1", AgentId = "a", AgentName = "n", Title = "mine" };
        var other = new Conversation { ConversationId = Guid.NewGuid(), UserId = "user-2", AgentId = "a", AgentName = "n", Title = "other" };
        var desktop = new Conversation { ConversationId = Guid.NewGuid(), UserId = null, AgentId = "a", AgentName = "n", Title = "desktop" };
        await store.SaveConversationAsync(mine);
        await store.SaveConversationAsync(other);
        await store.SaveConversationAsync(desktop);

        // 显式 userId 查询：命中原样匹配的 mine + NULL 行 desktop（SQLite 权威语义
        // `@uid IS NULL OR user_id IS NULL OR user_id=@uid` 的第二析取支与 @uid 无关——
        // NULL user_id 行对任何查询可见），只排除他人的 other。
        var summaries = await store.GetSummariesAsync("user-1");
        Assert.Equal(2, summaries.Count);
        Assert.Contains(summaries, s => s.ConversationId == mine.ConversationId);
        Assert.Contains(summaries, s => s.ConversationId == desktop.ConversationId);
        Assert.DoesNotContain(summaries, s => s.ConversationId == other.ConversationId);

        // null 查询（SQLite 的 @uid IS NULL 分支）：全可见（无过滤语义）。
        var all = await store.GetSummariesAsync(null!);
        Assert.Equal(3, all.Count);
    }
}
