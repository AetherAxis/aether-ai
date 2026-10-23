using System.Globalization;
using Aether.AI.Memory.Sqlite;
using Aether.AI.Core.Conversations;
using Aether.AI.Core.Conversations.Runs;
using Microsoft.Data.Sqlite;

namespace Aether.AI.Memory.Stores;

/// <summary>
/// SQLite-backed <see cref="IConversationStore"/>. The transcript is a typed, append-only event
/// stream: <c>conversation</c> (metadata + denormalized preview) + <c>agent_run</c> + <c>agent_event</c>.
/// Single-user (user_id column kept for the abstraction but the desktop passes empty).
/// WAL + busy_timeout + foreign_keys (cascade), lazy schema.
/// <para>
/// <b>旧库作废</b>：<see cref="MigrateAsync"/> 检测旧 flat <c>conversation_message</c> 表 → DROP 旧表整库重置
/// （用户已决定旧 <c>conversations.db</c> 历史会丢失）。全新库不受影响。
/// </para>
/// </summary>
public sealed class SqliteConversationStore : SqliteStoreBase, IConversationStore
{
    private const string ConvTable = "conversation";
    private const string RunTable = "agent_run";
    private const string EventTable = "agent_event";
    private const string OldMsgTable = "conversation_message"; // 旧 flat-message schema 签名

    private const string CreateConvSql =
        "CREATE TABLE IF NOT EXISTS " + ConvTable + " (" +
        "id TEXT NOT NULL PRIMARY KEY, user_id TEXT, agent_id TEXT NOT NULL, agent_name TEXT NOT NULL, " +
        "title TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL, " +
        "last_message_preview TEXT, last_message_at TEXT)";

    private const string CreateRunSql =
        "CREATE TABLE IF NOT EXISTS " + RunTable + " (" +
        "id TEXT NOT NULL PRIMARY KEY, conversation_id TEXT NOT NULL, seq INTEGER NOT NULL, " +
        "status INTEGER NOT NULL, agent_id TEXT NOT NULL, agent_name TEXT NOT NULL, model_id TEXT, " +
        "user_message TEXT NOT NULL, started_at TEXT NOT NULL, finished_at TEXT, " +
        "FOREIGN KEY(conversation_id) REFERENCES " + ConvTable + "(id) ON DELETE CASCADE)";

    private const string CreateEventSql =
        "CREATE TABLE IF NOT EXISTS " + EventTable + " (" +
        "id TEXT NOT NULL PRIMARY KEY, run_id TEXT NOT NULL, seq INTEGER NOT NULL, " +
        "event_type INTEGER NOT NULL, payload TEXT NOT NULL, created_at TEXT NOT NULL, " +
        "FOREIGN KEY(run_id) REFERENCES " + RunTable + "(id) ON DELETE CASCADE)";

    private const string CreateConvUpdatedIdx =
        "CREATE INDEX IF NOT EXISTS idx_conversation_updated ON " + ConvTable + "(updated_at DESC)";
    private const string CreateRunConvIdx =
        "CREATE INDEX IF NOT EXISTS idx_run_conv ON " + RunTable + "(conversation_id, seq)";
    private const string CreateEventRunIdx =
        "CREATE INDEX IF NOT EXISTS idx_event_run ON " + EventTable + "(run_id, seq)";

    private const string CreateSchemaSql =
        CreateConvSql + ";" + CreateRunSql + ";" + CreateEventSql + ";" +
        CreateConvUpdatedIdx + ";" + CreateRunConvIdx + ";" + CreateEventRunIdx;

    public SqliteConversationStore(string connectionString) : base(connectionString) { }

    public static SqliteConversationStore ForFile(string dbPath) => new(BuildConnectionString(dbPath));

    // FK ON DELETE CASCADE on agent_event→agent_run→conversation; enforce on every connection.
    protected override bool ForeignKeysEnabled => true;

    public async Task SaveConversationAsync(Conversation conversation, CancellationToken cancellationToken = default)
    {
        if (conversation is null) throw new ArgumentNullException(nameof(conversation));
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await EnsureSchemaAsync(CreateSchemaSql, cancellationToken);
        await using var c = await OpenAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            "INSERT INTO " + ConvTable + " (id,user_id,agent_id,agent_name,title,created_at,updated_at,last_message_preview,last_message_at) " +
            "VALUES (@id,@uid,@aid,@an,@t,@ca,@ua,@pv,@la) " +
            "ON CONFLICT(id) DO UPDATE SET user_id=@uid, agent_id=@aid, agent_name=@an, title=@t, " +
            "updated_at=@ua, last_message_preview=@pv, last_message_at=@la";
        cmd.Parameters.AddWithValue("@id", conversation.ConversationId.ToString());
        cmd.Parameters.AddWithValue("@uid", (object?)conversation.UserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@aid", conversation.AgentId ?? string.Empty);
        cmd.Parameters.AddWithValue("@an", conversation.AgentName ?? string.Empty);
        cmd.Parameters.AddWithValue("@t", conversation.Title ?? string.Empty);
        cmd.Parameters.AddWithValue("@ca", Format(conversation.CreatedAt));
        cmd.Parameters.AddWithValue("@ua", Format(conversation.UpdatedAt));
        cmd.Parameters.AddWithValue("@pv", (object?)conversation.LastMessagePreview ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@la", conversation.LastMessageAt is null ? DBNull.Value : (object)Format(conversation.LastMessageAt.Value));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<Conversation?> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(CreateSchemaSql, cancellationToken);
        await using var c = await OpenAsync(cancellationToken);
        return await ReadConversationAsync(c, conversationId, cancellationToken);
    }

    public async Task<IReadOnlyList<ConversationSummary>> GetSummariesAsync(string userId, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(CreateSchemaSql, cancellationToken);
        await using var c = await OpenAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        // preview 现已反范式化在 conversation 行上，无需子查询。
        cmd.CommandText =
            "SELECT id, title, agent_id, agent_name, updated_at, last_message_preview " +
            "FROM " + ConvTable + " " +
            "WHERE @uid IS NULL OR user_id IS NULL OR user_id=@uid " +
            "ORDER BY updated_at DESC";
        cmd.Parameters.AddWithValue("@uid", (object?)userId ?? DBNull.Value);

        var list = new List<ConversationSummary>();
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            list.Add(new ConversationSummary(
                Guid.Parse(r.GetString(0)),
                r.GetString(1),
                r.GetString(2),
                r.GetString(3),
                Parse(r.GetString(4)),
                r.IsDBNull(5) ? string.Empty : r.GetString(5)));
        }
        return list;
    }

    public async Task<IReadOnlyList<Conversation>> FindByUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(CreateSchemaSql, cancellationToken);
        await using var c = await OpenAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM " + ConvTable + " WHERE @uid IS NULL OR user_id IS NULL OR user_id=@uid ORDER BY updated_at DESC";
        cmd.Parameters.AddWithValue("@uid", (object?)userId ?? DBNull.Value);

        var ids = new List<Guid>();
        await using (var r = await cmd.ExecuteReaderAsync(cancellationToken))
            while (await r.ReadAsync(cancellationToken)) ids.Add(Guid.Parse(r.GetString(0)));

        var list = new List<Conversation>();
        foreach (var id in ids)
        {
            var conv = await ReadConversationAsync(c, id, cancellationToken);
            if (conv is not null) list.Add(conv);
        }
        return list;
    }

    public async Task<bool> DeleteAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(CreateSchemaSql, cancellationToken);
        await using var c = await OpenAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        // FK ON DELETE CASCADE: 删 conversation → 级联删 agent_run → agent_event。
        cmd.CommandText = "DELETE FROM " + ConvTable + " WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", conversationId.ToString());
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task AppendRunAsync(AgentRun run, IReadOnlyList<AgentEvent> events, CancellationToken cancellationToken = default)
    {
        if (run is null) throw new ArgumentNullException(nameof(run));
        if (events is null) throw new ArgumentNullException(nameof(events));

        await EnsureSchemaAsync(CreateSchemaSql, cancellationToken);
        await using var c = await OpenAsync(cancellationToken);
        await using var tx = await c.BeginTransactionAsync(cancellationToken);

        // 1) insert run
        await using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText =
                "INSERT INTO " + RunTable + " (id,conversation_id,seq,status,agent_id,agent_name,model_id,user_message,started_at,finished_at) " +
                "VALUES (@id,@cid,@seq,@st,@aid,@an,@mid,@um,@sa,@fa)";
            cmd.Parameters.AddWithValue("@id", run.Id.ToString());
            cmd.Parameters.AddWithValue("@cid", run.ConversationId.ToString());
            cmd.Parameters.AddWithValue("@seq", run.Seq);
            cmd.Parameters.AddWithValue("@st", (int)run.Status);
            cmd.Parameters.AddWithValue("@aid", run.AgentId ?? string.Empty);
            cmd.Parameters.AddWithValue("@an", run.AgentName ?? string.Empty);
            cmd.Parameters.AddWithValue("@mid", (object?)run.ModelId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@um", run.UserMessage ?? string.Empty);
            cmd.Parameters.AddWithValue("@sa", Format(run.StartedAt));
            cmd.Parameters.AddWithValue("@fa", run.FinishedAt is null ? DBNull.Value : (object)Format(run.FinishedAt.Value));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // 2) batch insert events
        foreach (var e in events)
        {
            await using var ins = c.CreateCommand();
            ins.Transaction = (SqliteTransaction)tx;
            ins.CommandText =
                "INSERT INTO " + EventTable + " (id,run_id,seq,event_type,payload,created_at) " +
                "VALUES (@id,@rid,@seq,@et,@pl,@ca)";
            ins.Parameters.AddWithValue("@id", e.Id.ToString());
            ins.Parameters.AddWithValue("@rid", run.Id.ToString());
            ins.Parameters.AddWithValue("@seq", e.Seq);
            ins.Parameters.AddWithValue("@et", (int)e.EventType);
            ins.Parameters.AddWithValue("@pl", e.Payload ?? string.Empty);
            ins.Parameters.AddWithValue("@ca", Format(e.CreatedAt));
            await ins.ExecuteNonQueryAsync(cancellationToken);
        }

        // 3) 反范式化更新 conversation preview/updatedAt
        var preview = ComputePreview(run, events);
        await using (var upd = c.CreateCommand())
        {
            upd.Transaction = (SqliteTransaction)tx;
            upd.CommandText =
                "UPDATE " + ConvTable + " SET last_message_preview=@pv, last_message_at=@la, updated_at=@ua " +
                "WHERE id=@cid";
            upd.Parameters.AddWithValue("@pv", (object?)preview ?? DBNull.Value);
            var lastAt = run.FinishedAt ?? run.StartedAt;
            upd.Parameters.AddWithValue("@la", Format(lastAt));
            upd.Parameters.AddWithValue("@ua", Format(DateTimeOffset.UtcNow));
            upd.Parameters.AddWithValue("@cid", run.ConversationId.ToString());
            await upd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentRun>> GetRunsAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(CreateSchemaSql, cancellationToken);
        await using var c = await OpenAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            "SELECT id,conversation_id,seq,status,agent_id,agent_name,model_id,user_message,started_at,finished_at " +
            "FROM " + RunTable + " WHERE conversation_id=@cid ORDER BY seq";
        cmd.Parameters.AddWithValue("@cid", conversationId.ToString());

        var list = new List<AgentRun>();
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            list.Add(new AgentRun
            {
                Id = Guid.Parse(r.GetString(0)),
                ConversationId = Guid.Parse(r.GetString(1)),
                Seq = r.GetInt32(2),
                Status = (RunStatus)r.GetInt32(3),
                AgentId = r.GetString(4),
                AgentName = r.GetString(5),
                ModelId = r.IsDBNull(6) ? null : r.GetString(6),
                UserMessage = r.GetString(7),
                StartedAt = Parse(r.GetString(8)),
                FinishedAt = r.IsDBNull(9) ? null : Parse(r.GetString(9)),
            });
        }
        return list;
    }

    public async Task<IReadOnlyList<AgentEvent>> GetEventsAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(CreateSchemaSql, cancellationToken);
        await using var c = await OpenAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            "SELECT id,run_id,seq,event_type,payload,created_at " +
            "FROM " + EventTable + " WHERE run_id=@rid ORDER BY seq";
        cmd.Parameters.AddWithValue("@rid", runId.ToString());

        var list = new List<AgentEvent>();
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            list.Add(new AgentEvent
            {
                Id = Guid.Parse(r.GetString(0)),
                RunId = Guid.Parse(r.GetString(1)),
                Seq = r.GetInt32(2),
                EventType = (AgentEventType)r.GetInt32(3),
                Payload = r.GetString(4),
                CreatedAt = Parse(r.GetString(5)),
            });
        }
        return list;
    }

    public async Task<IReadOnlyList<RunSummary>> GetRunsInRangeAsync(string userId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(CreateSchemaSql, cancellationToken);
        await using var c = await OpenAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        // join conversation 取 user_id 过滤 + title；started_at 是 ISO 文本串，字符串比较对 ISO 排序正确。
        cmd.CommandText =
            "SELECT r.id, r.conversation_id, c.title, r.agent_id, r.agent_name, r.status, r.started_at, r.finished_at, r.user_message " +
            "FROM " + RunTable + " r JOIN " + ConvTable + " c ON r.conversation_id = c.id " +
            "WHERE (@uid IS NULL OR c.user_id IS NULL OR c.user_id = @uid) AND r.started_at >= @from AND r.started_at < @to " +
            "ORDER BY r.started_at";
        cmd.Parameters.AddWithValue("@uid", userId ?? string.Empty);
        cmd.Parameters.AddWithValue("@from", Format(from));
        cmd.Parameters.AddWithValue("@to", Format(to));

        var list = new List<RunSummary>();
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            list.Add(new RunSummary(
                Guid.Parse(r.GetString(0)),
                Guid.Parse(r.GetString(1)),
                r.IsDBNull(2) ? string.Empty : r.GetString(2),
                r.IsDBNull(3) ? string.Empty : r.GetString(3),
                r.IsDBNull(4) ? string.Empty : r.GetString(4),
                (RunStatus)r.GetInt32(5),
                Parse(r.GetString(6)),
                r.IsDBNull(7) ? null : Parse(r.GetString(7)),
                r.IsDBNull(8) ? string.Empty : r.GetString(8)));
        }
        return list;
    }

    private static async Task<Conversation?> ReadConversationAsync(SqliteConnection c, Guid id, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            "SELECT id,user_id,agent_id,agent_name,title,created_at,updated_at,last_message_preview,last_message_at " +
            "FROM " + ConvTable + " WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new Conversation
        {
            ConversationId = Guid.Parse(r.GetString(0)),
            UserId = r.IsDBNull(1) ? null : r.GetString(1),
            AgentId = r.GetString(2),
            AgentName = r.GetString(3),
            Title = r.GetString(4),
            CreatedAt = Parse(r.GetString(5)),
            UpdatedAt = Parse(r.GetString(6)),
            LastMessagePreview = r.IsDBNull(7) ? null : r.GetString(7),
            LastMessageAt = r.IsDBNull(8) ? null : Parse(r.GetString(8)),
        };
    }

    private static string ComputePreview(AgentRun run, IReadOnlyList<AgentEvent> events)
    {
        // 优先最后一条答案文本；否则回退到用户消息。
        for (var i = events.Count - 1; i >= 0; i--)
        {
            if (events[i].EventType == AgentEventType.TextChunk)
            {
                var text = AgentEventPayload.Deserialize<AgentEventPayload.TextChunk>(events[i].Payload)?.Text;
                if (!string.IsNullOrWhiteSpace(text))
                    return Truncate(text.Replace('\n', ' ').Trim(), 80);
            }
        }
        return Truncate((run.UserMessage ?? string.Empty).Replace('\n', ' ').Trim(), 80);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// 迁移：检测旧 flat <c>conversation_message</c> 表（旧 schema 签名）→ 整库重置（DROP 旧表 + 重建新 schema）。
    /// 用户已决定旧 <c>conversations.db</c> 历史作废。全新库无 <c>conversation_message</c> → 无操作。
    /// <para>
    /// 另补 <c>UNIQUE(conversation_id, seq)</c>（唯一索引）——seq TOCTOU 守卫：并发 <see cref="AppendRunAsync"/>
    /// （桌面/手机多路径同时落库）各自算 max+1 可能拿到同一 seq 写重序。唯一定位索引而非表级约束，
    /// 老库无需重建表即可补上；建前先清可能已存在的重复（并发窗口曾写入），保留每个 (conv,seq) 最新一行。
    /// </para>
    /// </summary>
    protected override async Task MigrateAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (await TableExistsAsync(connection, OldMsgTable, ct))
        {
            // 旧 flat-message schema → 整库重置。
            await ExecAsync(connection, "DROP TABLE IF EXISTS " + OldMsgTable, ct);
            await ExecAsync(connection, "DROP TABLE IF EXISTS " + EventTable, ct);
            await ExecAsync(connection, "DROP TABLE IF EXISTS " + RunTable, ct);
            await ExecAsync(connection, "DROP TABLE IF EXISTS " + ConvTable, ct);
            await ExecAsync(connection, CreateSchemaSql, ct);
        }

        // seq TOCTOU 守卫：先清重复（保留每 (conv,seq) 最后插入的一行，rowid 是插入序）、再建唯一
        // 索引——两条都幂等，新库（含上方整库重置后的新 schema）与既有库同路径。
        await ExecAsync(connection,
            "DELETE FROM " + RunTable + " WHERE rowid NOT IN " +
            "(SELECT MAX(rowid) FROM " + RunTable + " GROUP BY conversation_id, seq)", ct);
        await ExecAsync(connection,
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_run_conv_seq ON " + RunTable + "(conversation_id, seq)", ct);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection c, string name, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@n";
        cmd.Parameters.AddWithValue("@n", name);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task ExecAsync(SqliteConnection c, string sql, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Format(DateTimeOffset dto) => dto.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
