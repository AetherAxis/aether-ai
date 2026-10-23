using Aether.AI.Core.Conversations;
using Aether.AI.Core.Conversations.Runs;
using Aether.AI.Memory.Stores;
using Microsoft.Data.Sqlite;

namespace Aether.AI.Memory.Tests;

public class SqliteConversationStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConversationStore _store;

    public SqliteConversationStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"conv-test-{Guid.NewGuid():N}.db");
        _store = SqliteConversationStore.ForFile(_dbPath);
    }

    [Fact]
    public async Task Save_Get_conversation_round_trip()
    {
        var conv = new Conversation
        {
            ConversationId = Guid.NewGuid(),
            UserId = "",
            AgentId = "agent-1",
            AgentName = "Tester",
            Title = "你好",
        };

        await _store.SaveConversationAsync(conv);

        var loaded = await _store.GetConversationAsync(conv.ConversationId);
        Assert.NotNull(loaded);
        Assert.Equal("Tester", loaded!.AgentName);
        Assert.Equal("agent-1", loaded.AgentId);
        Assert.Equal("你好", loaded.Title);
    }

    [Fact]
    public async Task AppendRun_persists_run_and_events_and_updates_preview()
    {
        var conv = new Conversation
        {
            ConversationId = Guid.NewGuid(),
            UserId = "",
            AgentId = "agent-1",
            AgentName = "Tester",
            Title = "t",
        };
        await _store.SaveConversationAsync(conv);

        var run = new AgentRun
        {
            ConversationId = conv.ConversationId,
            Seq = 1,
            Status = RunStatus.Completed,
            AgentId = conv.AgentId,
            AgentName = conv.AgentName,
            UserMessage = "hi",
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow,
        };
        var events = new List<AgentEvent>
        {
            new()
            {
                RunId = run.Id,
                Seq = 1,
                EventType = AgentEventType.TextChunk,
                Payload = AgentEventPayload.Serialize(new AgentEventPayload.TextChunk("hello there")),
            },
        };

        await _store.AppendRunAsync(run, events);

        // 读回 run + events
        var runs = await _store.GetRunsAsync(conv.ConversationId);
        Assert.Single(runs);
        Assert.Equal("hi", runs[0].UserMessage);

        var loadedEvents = await _store.GetEventsAsync(runs[0].Id);
        Assert.Single(loadedEvents);
        Assert.Equal(AgentEventType.TextChunk, loadedEvents[0].EventType);
        var text = AgentEventPayload.Deserialize<AgentEventPayload.TextChunk>(loadedEvents[0].Payload);
        Assert.Equal("hello there", text?.Text);

        // preview/updatedAt 反范式化更新
        var loadedConv = await _store.GetConversationAsync(conv.ConversationId);
        Assert.NotNull(loadedConv);
        Assert.Equal("hello there", loadedConv!.LastMessagePreview);
        Assert.NotNull(loadedConv.LastMessageAt);
    }

    [Fact]
    public async Task GetSummaries_orders_by_updated_desc_and_includes_preview()
    {
        var old = new Conversation { ConversationId = Guid.NewGuid(), UserId = "", AgentId = "a", AgentName = "A", Title = "old" };
        await _store.SaveConversationAsync(old);
        await AppendTextRunAsync(old.ConversationId, 1, "old message", startedAt: DateTimeOffset.UtcNow.AddHours(-1));

        var recent = new Conversation { ConversationId = Guid.NewGuid(), UserId = "", AgentId = "a", AgentName = "A", Title = "recent" };
        await _store.SaveConversationAsync(recent);
        await AppendTextRunAsync(recent.ConversationId, 1, "recent message", startedAt: DateTimeOffset.UtcNow);

        var summaries = await _store.GetSummariesAsync("");
        Assert.Equal(2, summaries.Count);
        Assert.Equal(recent.ConversationId, summaries[0].ConversationId); // newer first
        Assert.Equal("recent message", summaries[0].LastMessagePreview);
        Assert.Equal("old message", summaries[1].LastMessagePreview);
    }

    [Fact]
    public async Task Delete_removes_conversation_runs_and_events()
    {
        var conv = new Conversation { ConversationId = Guid.NewGuid(), UserId = "", AgentId = "a", AgentName = "A", Title = "t" };
        await _store.SaveConversationAsync(conv);
        await AppendTextRunAsync(conv.ConversationId, 1, "x");

        Assert.True(await _store.DeleteAsync(conv.ConversationId));
        Assert.Null(await _store.GetConversationAsync(conv.ConversationId));
        Assert.Empty(await _store.GetRunsAsync(conv.ConversationId));
        Assert.Empty(await _store.GetSummariesAsync(""));
    }

    [Fact]
    public async Task Runs_ordered_by_seq_and_events_ordered_by_seq()
    {
        var conv = new Conversation { ConversationId = Guid.NewGuid(), UserId = "", AgentId = "a", AgentName = "A", Title = "t" };
        await _store.SaveConversationAsync(conv);

        // 故意乱序 append（seq 2 先、seq 1 后），读回应按 seq 升序。
        await AppendTextRunAsync(conv.ConversationId, 2, "second");
        await AppendTextRunAsync(conv.ConversationId, 1, "first");

        var runs = await _store.GetRunsAsync(conv.ConversationId);
        Assert.Equal(2, runs.Count);
        Assert.Equal(1, runs[0].Seq);
        Assert.Equal("first", runs[0].UserMessage);
        Assert.Equal(2, runs[1].Seq);
        Assert.Equal("second", runs[1].UserMessage);
    }

    [Fact]
    public async Task AppendRun_same_seq_within_conversation_is_rejected()
    {
        // seq TOCTOU 守卫：并发两路 AppendRunAsync 各算 max+1 可能拿到同一 seq 写重序——
        // UNIQUE(conversation_id, seq) 必须让第二个落库失败（而不是静默覆盖/双行）。
        var conv = new Conversation { ConversationId = Guid.NewGuid(), UserId = "", AgentId = "a", AgentName = "A", Title = "t" };
        await _store.SaveConversationAsync(conv);

        await AppendTextRunAsync(conv.ConversationId, 1, "first");
        await Assert.ThrowsAsync<SqliteException>(() => AppendTextRunAsync(conv.ConversationId, 1, "dup"));
    }

    [Fact]
    public async Task Migrate_dedups_preexisting_duplicate_seq_rows()
    {
        // 手工造「旧库」：事件流 schema 但无唯一索引 + 同 (conv, seq) 两行（TOCTOU 窗口曾发生）。
        // 新 store 首次打开时 MigrateAsync 清重复（保留最后插入的一行）+ 建唯一索引。
        var path = Path.Combine(Path.GetTempPath(), $"conv-mig-{Guid.NewGuid():N}.db");
        try
        {
            var convId = Guid.NewGuid();
            await using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                await conn.OpenAsync();
                await ExecRaw(conn,
                    "CREATE TABLE conversation (id TEXT NOT NULL PRIMARY KEY, user_id TEXT, agent_id TEXT NOT NULL, agent_name TEXT NOT NULL, title TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL, last_message_preview TEXT, last_message_at TEXT); " +
                    "CREATE TABLE agent_run (id TEXT NOT NULL PRIMARY KEY, conversation_id TEXT NOT NULL, seq INTEGER NOT NULL, status INTEGER NOT NULL, agent_id TEXT NOT NULL, agent_name TEXT NOT NULL, model_id TEXT, user_message TEXT NOT NULL, started_at TEXT NOT NULL, finished_at TEXT); " +
                    "CREATE TABLE agent_event (id TEXT NOT NULL PRIMARY KEY, run_id TEXT NOT NULL, seq INTEGER NOT NULL, event_type INTEGER NOT NULL, payload TEXT NOT NULL, created_at TEXT NOT NULL)");
                for (var i = 0; i < 2; i++)
                {
                    await ExecRaw(conn,
                        "INSERT INTO agent_run (id, conversation_id, seq, status, agent_id, agent_name, model_id, user_message, started_at, finished_at) " +
                        $"VALUES ('{Guid.NewGuid():N}', '{convId}', 1, 3, 'a', 'A', NULL, 'fire-{i}', '2026-01-0{i + 1}T00:00:00.0000000+00:00', NULL)");
                    // conversation_id 用默认 ToString()（连字符 D 格式）——store 读写各处一致；N 格式会匹配不上。
                }
            }

            // 首次打开触发 EnsureSchemaAsync → MigrateAsync（dedup + 唯一索引）。
            var store = SqliteConversationStore.ForFile(path);
            var runs = await store.GetRunsAsync(convId);
            Assert.Single(runs);
            Assert.Equal("fire-1", runs[0].UserMessage); // 按 rowid 保留最后插入的那行

            // 唯一索引已生效：同 (conv, seq) 再落库被拒。
            var run = new AgentRun
            {
                ConversationId = convId,
                Seq = 1,
                Status = RunStatus.Completed,
                AgentId = "a",
                AgentName = "A",
                UserMessage = "dup",
                StartedAt = DateTimeOffset.UtcNow,
                FinishedAt = DateTimeOffset.UtcNow,
            };
            await Assert.ThrowsAsync<SqliteException>(() =>
                store.AppendRunAsync(run, Array.Empty<AgentEvent>()));
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            try { if (File.Exists(path + "-wal")) File.Delete(path + "-wal"); } catch { }
            try { if (File.Exists(path + "-shm")) File.Delete(path + "-shm"); } catch { }
        }
    }

    private static async Task ExecRaw(SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task AppendTextRunAsync(Guid conversationId, int seq, string text, DateTimeOffset? startedAt = null)
    {
        var at = startedAt ?? DateTimeOffset.UtcNow;
        var run = new AgentRun
        {
            ConversationId = conversationId,
            Seq = seq,
            Status = RunStatus.Completed,
            AgentId = "a",
            AgentName = "A",
            UserMessage = text,
            StartedAt = at,
            FinishedAt = at,
        };
        var events = new List<AgentEvent>
        {
            new()
            {
                RunId = run.Id,
                Seq = 1,
                EventType = AgentEventType.TextChunk,
                Payload = AgentEventPayload.Serialize(new AgentEventPayload.TextChunk(text)),
            },
        };
        await _store.AppendRunAsync(run, events);
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal"); } catch { }
        try { if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm"); } catch { }
    }
}
