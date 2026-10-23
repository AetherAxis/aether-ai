using System.Globalization;
using Aether.AI.Memory.Sqlite;
using Aether.AI.Core.Memory;
using Microsoft.Data.Sqlite;

namespace Aether.AI.Memory.Stores;

/// <summary>
/// SQLite-backed <see cref="IMemoryStore"/> for long-term agent memory. Table <c>agent_memory</c>.
/// WAL + busy_timeout, lazy schema. Mirrors <see cref="SqliteConversationStore"/>'s pattern (no EF Core).
/// <para>Search is a simple substring (LIKE) match over key/content — no embeddings. Sufficient for
/// the single-user desktop case; a vector backend can implement <see cref="IMemoryStore"/> separately later.</para>
/// </summary>
public sealed class SqliteMemoryStore : SqliteStoreBase, IMemoryStore
{
    private const string Table = "agent_memory";

    private const string CreateTableSql =
        "CREATE TABLE IF NOT EXISTS " + Table + " (" +
        "id TEXT NOT NULL PRIMARY KEY, user_id TEXT, type INTEGER NOT NULL, key TEXT NOT NULL, " +
        "content TEXT NOT NULL, importance REAL NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL, metadata TEXT)";

    private const string CreateUserIdx =
        "CREATE INDEX IF NOT EXISTS idx_memory_user ON " + Table + "(user_id)";

    private const string CreateSchemaSql = CreateTableSql + ";" + CreateUserIdx;

    public SqliteMemoryStore(string connectionString) : base(connectionString) { }

    public static SqliteMemoryStore ForFile(string dbPath) => new(BuildConnectionString(dbPath));

    public async Task SaveAsync(MemoryItem item, CancellationToken cancellationToken = default)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        await EnsureSchemaAsync(CreateSchemaSql, cancellationToken);
        await using var c = await OpenAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        // upsert by id
        cmd.CommandText =
            "INSERT INTO " + Table + " (id,user_id,type,key,content,importance,created_at,updated_at,metadata) " +
            "VALUES (@id,@uid,@t,@k,@c,@imp,@ca,@ua,@meta) " +
            "ON CONFLICT(id) DO UPDATE SET user_id=@uid, type=@t, key=@k, content=@c, importance=@imp, updated_at=@ua, metadata=@meta";
        cmd.Parameters.AddWithValue("@id", item.Id.ToString());
        cmd.Parameters.AddWithValue("@uid", (object?)item.UserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@t", (int)item.Type);
        cmd.Parameters.AddWithValue("@k", item.Key ?? string.Empty);
        cmd.Parameters.AddWithValue("@c", item.Content ?? string.Empty);
        cmd.Parameters.AddWithValue("@imp", item.Importance);
        cmd.Parameters.AddWithValue("@ca", Format(item.CreatedAt));
        cmd.Parameters.AddWithValue("@ua", Format(item.UpdatedAt));
        cmd.Parameters.AddWithValue("@meta", (object?)MetadataToJson(item.Metadata) ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<MemoryItem?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(CreateSchemaSql, cancellationToken);
        await using var c = await OpenAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,user_id,type,key,content,importance,created_at,updated_at,metadata FROM " + Table + " WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        return await r.ReadAsync(cancellationToken) ? Read(r) : null;
    }

    public async Task<IReadOnlyList<MemoryItem>> FindByUserAsync(string? userId, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(CreateSchemaSql, cancellationToken);
        await using var c = await OpenAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        if (userId is null)
        {
            cmd.CommandText = "SELECT id,user_id,type,key,content,importance,created_at,updated_at,metadata FROM " + Table + " WHERE user_id IS NULL ORDER BY updated_at DESC";
        }
        else
        {
            cmd.CommandText = "SELECT id,user_id,type,key,content,importance,created_at,updated_at,metadata FROM " + Table + " WHERE user_id=@uid ORDER BY updated_at DESC";
            cmd.Parameters.AddWithValue("@uid", userId);
        }
        return await ReadAllAsync(cmd, cancellationToken);
    }

    public async Task<IReadOnlyList<MemoryItem>> FindByTypeAsync(string? userId, MemoryType type, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(CreateSchemaSql, cancellationToken);
        await using var c = await OpenAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = userId is null
            ? "SELECT id,user_id,type,key,content,importance,created_at,updated_at,metadata FROM " + Table + " WHERE user_id IS NULL AND type=@t ORDER BY updated_at DESC"
            : "SELECT id,user_id,type,key,content,importance,created_at,updated_at,metadata FROM " + Table + " WHERE user_id=@uid AND type=@t ORDER BY updated_at DESC";
        if (userId is not null) cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@t", (int)type);
        return await ReadAllAsync(cmd, cancellationToken);
    }

    public async Task<IReadOnlyList<MemoryItem>> SearchAsync(string? userId, string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return await FindByUserAsync(userId, cancellationToken);
        await EnsureSchemaAsync(CreateSchemaSql, cancellationToken);
        await using var c = await OpenAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        var q = $"%{query}%";
        if (userId is null)
        {
            cmd.CommandText = "SELECT id,user_id,type,key,content,importance,created_at,updated_at,metadata FROM " + Table + " WHERE user_id IS NULL AND (key LIKE @q OR content LIKE @q) ORDER BY updated_at DESC";
        }
        else
        {
            cmd.CommandText = "SELECT id,user_id,type,key,content,importance,created_at,updated_at,metadata FROM " + Table + " WHERE user_id=@uid AND (key LIKE @q OR content LIKE @q) ORDER BY updated_at DESC";
            cmd.Parameters.AddWithValue("@uid", userId);
        }
        cmd.Parameters.AddWithValue("@q", q);
        return await ReadAllAsync(cmd, cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(CreateSchemaSql, cancellationToken);
        await using var c = await OpenAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM " + Table + " WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<int> ClearAsync(string? userId, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(CreateSchemaSql, cancellationToken);
        await using var c = await OpenAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        if (userId is null)
        {
            cmd.CommandText = "DELETE FROM " + Table;
        }
        else
        {
            cmd.CommandText = "DELETE FROM " + Table + " WHERE user_id=@uid";
            cmd.Parameters.AddWithValue("@uid", userId);
        }
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<MemoryItem>> ReadAllAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var list = new List<MemoryItem>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    private static MemoryItem Read(SqliteDataReader r)
    {
        var item = new MemoryItem
        {
            Id = Guid.Parse(r.GetString(0)),
            UserId = r.IsDBNull(1) ? null : r.GetString(1),
            Type = (MemoryType)r.GetInt32(2),
            Key = r.GetString(3),
            Content = r.GetString(4),
            Importance = r.GetDouble(5),
            CreatedAt = Parse(r.GetString(6)),
            UpdatedAt = Parse(r.GetString(7)),
        };
        if (!r.IsDBNull(8))
        {
            foreach (var kv in MetadataFromJson(r.GetString(8)))
                item.Metadata[kv.Key] = kv.Value;
        }
        return item;
    }

    private static string? MetadataToJson(IReadOnlyDictionary<string, string?> meta)
        => meta is null || meta.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(meta);

    private static Dictionary<string, string?> MetadataFromJson(string json)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(json) ?? new(); }
        catch { return new(); }
    }

    private static string Format(DateTimeOffset dto) => dto.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
