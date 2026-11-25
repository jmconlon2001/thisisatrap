using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace ThisIsATrap;

public sealed record MessageSummary(
    string Id,
    DateTimeOffset ReceivedUtc,
    string From,
    string To,
    string Subject,
    string Preview,
    long Size,
    int AttachmentCount,
    bool IsRead,
    string Inbox = Inbox.DefaultSlug);

public sealed record MessageRecord(
    string Id,
    DateTimeOffset ReceivedUtc,
    string From,
    string To,
    string Cc,
    string Subject,
    string Preview,
    long Size,
    int AttachmentCount,
    bool IsRead,
    string EnvelopeFrom,
    string EnvelopeTo,
    string ClientIp,
    long InboxId = Inbox.DefaultId);

public sealed record MailStats(int Total, int Unread, long TotalBytes);

/// <summary>
/// Message index in SQLite plus the raw .eml for each message on disk.
/// The .eml is the source of truth for bodies and attachments; the index is just for fast listing.
/// </summary>
public sealed class MailStore
{
    private readonly string _connectionString;
    private readonly string _messageDir;
    private readonly ILogger<MailStore> _log;
    private readonly TimeProvider _time;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string SummaryColumns =
        "id, received, from_addr, to_addrs, subject, preview, size, attachment_count, is_read, (SELECT slug FROM inboxes WHERE inboxes.id = messages.inbox_id)";

    public MailStore(IOptions<TrapOptions> options, IWebHostEnvironment env, ILogger<MailStore> log, TimeProvider? time = null)
    {
        _log = log;
        _time = time ?? TimeProvider.System;
        var dataPath = options.Value.DataPath;
        if (!Path.IsPathRooted(dataPath)) dataPath = Path.Combine(env.ContentRootPath, dataPath);
        DataPath = Path.GetFullPath(dataPath);
        _messageDir = Path.Combine(DataPath, "messages");
        Directory.CreateDirectory(_messageDir);
        var dbPath = Path.Combine(DataPath, DatabaseFile);
        MigrateLegacyDatabase(dbPath);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 30,
        }.ToString();

        using var db = Open();
        Exec(db, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS messages (
                id TEXT PRIMARY KEY,
                received INTEGER NOT NULL,
                from_addr TEXT NOT NULL,
                to_addrs TEXT NOT NULL,
                cc_addrs TEXT NOT NULL,
                subject TEXT NOT NULL,
                preview TEXT NOT NULL,
                size INTEGER NOT NULL,
                attachment_count INTEGER NOT NULL,
                is_read INTEGER NOT NULL DEFAULT 0,
                envelope_from TEXT NOT NULL,
                envelope_to TEXT NOT NULL,
                client_ip TEXT NOT NULL,
                inbox_id INTEGER NOT NULL DEFAULT 1
            );
            CREATE INDEX IF NOT EXISTS ix_messages_received ON messages(received DESC);
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS inboxes (
                id INTEGER PRIMARY KEY,
                slug TEXT NOT NULL UNIQUE,
                name TEXT NOT NULL,
                created INTEGER NOT NULL,
                purge TEXT NULL
            );
            """);

        // Databases from before inboxes existed: every existing message belongs to the Default inbox.
        if (!ColumnExists(db, "messages", "inbox_id"))
        {
            Exec(db, "ALTER TABLE messages ADD COLUMN inbox_id INTEGER NOT NULL DEFAULT 1");
            _log.LogInformation("Migrated message index: added inboxes (existing mail is in the Default inbox)");
        }
        Exec(db, "CREATE INDEX IF NOT EXISTS ix_messages_inbox ON messages(inbox_id, received DESC)");

        using var seed = db.CreateCommand();
        seed.CommandText = "INSERT OR IGNORE INTO inboxes (id, slug, name, created) VALUES ($id, $slug, $name, $now)";
        seed.Parameters.AddWithValue("$id", Inbox.DefaultId);
        seed.Parameters.AddWithValue("$slug", Inbox.DefaultSlug);
        seed.Parameters.AddWithValue("$name", Inbox.DefaultName);
        seed.Parameters.AddWithValue("$now", _time.GetUtcNow().ToUnixTimeMilliseconds());
        seed.ExecuteNonQuery();
    }

    public const string DatabaseFile = "thisisatrap.db";
    public const string LegacyDatabaseFile = "mailtrap.db";

    public string DataPath { get; }

    /// <summary>Size of the SQLite index including its WAL files.</summary>
    public long DatabaseBytes() => new[] { "", "-wal", "-shm" }
        .Select(s => new FileInfo(Path.Combine(DataPath, DatabaseFile + s)))
        .Where(f => f.Exists)
        .Sum(f => f.Length);

    /// <summary>Data folders created before the rename hold mailtrap.db (+ WAL files); adopt them under the new name.</summary>
    private void MigrateLegacyDatabase(string dbPath)
    {
        var legacy = Path.Combine(DataPath, LegacyDatabaseFile);
        if (File.Exists(dbPath) || !File.Exists(legacy)) return;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(legacy + suffix)) File.Move(legacy + suffix, dbPath + suffix);
        _log.LogInformation("Renamed legacy database {Legacy} to {Database}", legacy, dbPath);
    }

    public string RawPath(string id) => Path.Combine(_messageDir, SafeId(id) + ".eml");

    // ------------------------------------------------------------------ messages

    public async Task AddAsync(MessageRecord m, Stream raw, CancellationToken ct)
    {
        await using (var file = File.Create(RawPath(m.Id)))
            await raw.CopyToAsync(file, ct);

        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO messages (id, received, from_addr, to_addrs, cc_addrs, subject, preview, size, attachment_count, is_read, envelope_from, envelope_to, client_ip, inbox_id)
            VALUES ($id, $received, $from, $to, $cc, $subject, $preview, $size, $att, 0, $efrom, $eto, $ip, $inbox)
            """;
        cmd.Parameters.AddWithValue("$id", m.Id);
        cmd.Parameters.AddWithValue("$received", m.ReceivedUtc.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$from", m.From);
        cmd.Parameters.AddWithValue("$to", m.To);
        cmd.Parameters.AddWithValue("$cc", m.Cc);
        cmd.Parameters.AddWithValue("$subject", m.Subject);
        cmd.Parameters.AddWithValue("$preview", m.Preview);
        cmd.Parameters.AddWithValue("$size", m.Size);
        cmd.Parameters.AddWithValue("$att", m.AttachmentCount);
        cmd.Parameters.AddWithValue("$efrom", m.EnvelopeFrom);
        cmd.Parameters.AddWithValue("$eto", m.EnvelopeTo);
        cmd.Parameters.AddWithValue("$ip", m.ClientIp);
        cmd.Parameters.AddWithValue("$inbox", m.InboxId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public (List<MessageSummary> Items, int Total) List(string? search, bool unreadOnly, bool withAttachments, int skip, int take, long? inboxId = null)
    {
        using var db = Open();
        var where = new List<string>();
        using var cmd = db.CreateCommand();
        if (!string.IsNullOrWhiteSpace(search))
        {
            where.Add("(subject LIKE $q ESCAPE '\\' OR from_addr LIKE $q ESCAPE '\\' OR to_addrs LIKE $q ESCAPE '\\' OR cc_addrs LIKE $q ESCAPE '\\' OR preview LIKE $q ESCAPE '\\' OR envelope_to LIKE $q ESCAPE '\\')");
            var escaped = search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            cmd.Parameters.AddWithValue("$q", $"%{escaped}%");
        }
        if (unreadOnly) where.Add("is_read = 0");
        if (withAttachments) where.Add("attachment_count > 0");
        if (inboxId is { } inbox)
        {
            where.Add("inbox_id = $inbox");
            cmd.Parameters.AddWithValue("$inbox", inbox);
        }
        var filter = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

        cmd.CommandText = $"SELECT COUNT(*) FROM messages {filter}";
        var total = Convert.ToInt32(cmd.ExecuteScalar());

        cmd.CommandText = $"SELECT {SummaryColumns} FROM messages {filter} ORDER BY received DESC LIMIT $take OFFSET $skip";
        cmd.Parameters.AddWithValue("$take", Math.Clamp(take, 1, 500));
        cmd.Parameters.AddWithValue("$skip", Math.Max(0, skip));
        var items = new List<MessageSummary>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) items.Add(ReadSummary(r));
        return (items, total);
    }

    public MessageRecord? Get(string id)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, received, from_addr, to_addrs, cc_addrs, subject, preview, size, attachment_count, is_read, envelope_from, envelope_to, client_ip, inbox_id
            FROM messages WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new MessageRecord(
            r.GetString(0), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)), r.GetString(2), r.GetString(3), r.GetString(4),
            r.GetString(5), r.GetString(6), r.GetInt64(7), r.GetInt32(8), r.GetInt32(9) != 0, r.GetString(10), r.GetString(11), r.GetString(12),
            r.GetInt64(13));
    }

    public void SetRead(string id, bool read)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE messages SET is_read = $r WHERE id = $id";
        cmd.Parameters.AddWithValue("$r", read ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void MarkAllRead(long? inboxId = null)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE messages SET is_read = 1 WHERE is_read = 0" + (inboxId is null ? "" : " AND inbox_id = $inbox");
        if (inboxId is { } i) cmd.Parameters.AddWithValue("$inbox", i);
        cmd.ExecuteNonQuery();
    }

    public bool Delete(string id) => DeleteIds([id]) > 0;

    public int DeleteAll(long? inboxId = null)
    {
        using var db = Open();
        var ids = inboxId is { } i
            ? ReadIds(db, "SELECT id FROM messages WHERE inbox_id = $inbox", ("$inbox", i))
            : ReadIds(db, "SELECT id FROM messages");
        return DeleteIds(ids);
    }

    /// <summary>Applies the age and count rules (to one inbox, or to all mail). Returns how many messages were removed.</summary>
    public int Purge(PurgeSettings s, long? inboxId = null)
    {
        using var db = Open();
        var scope = inboxId is null ? "" : " AND inbox_id = $inbox";
        var inbox = ("$inbox", (object)(inboxId ?? 0));
        var ids = new HashSet<string>();
        if (s.MaxAgeHours > 0)
        {
            var cutoff = _time.GetUtcNow().AddHours(-s.MaxAgeHours).ToUnixTimeMilliseconds();
            ids.UnionWith(ReadIds(db, $"SELECT id FROM messages WHERE received < $cutoff{scope}", ("$cutoff", cutoff), inbox));
        }
        if (s.MaxMessages > 0)
            ids.UnionWith(ReadIds(db, $"SELECT id FROM messages WHERE 1 = 1{scope} ORDER BY received DESC LIMIT -1 OFFSET $keep", ("$keep", s.MaxMessages), inbox));
        return ids.Count == 0 ? 0 : DeleteIds(ids);
    }

    public MailStats Stats(long? inboxId = null)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(1 - is_read), 0), COALESCE(SUM(size), 0) FROM messages" + (inboxId is null ? "" : " WHERE inbox_id = $inbox");
        if (inboxId is { } i) cmd.Parameters.AddWithValue("$inbox", i);
        using var r = cmd.ExecuteReader();
        r.Read();
        return new MailStats(r.GetInt32(0), r.GetInt32(1), r.GetInt64(2));
    }

    // ------------------------------------------------------------------ inboxes

    /// <summary>
    /// The inbox a message should land in: <paramref name="key"/> (SMTP username / routing header) normalised to a slug.
    /// Unknown slugs create an inbox when <paramref name="create"/> is set; otherwise (or when there is no key) mail goes to Default.
    /// </summary>
    public (Inbox Inbox, bool Created) ResolveInbox(string? key, bool create)
    {
        var slug = InboxSlug.Normalize(key);
        if (slug is null) return (GetInbox(Inbox.DefaultId)!, false);
        if (GetInbox(slug) is { } existing) return (existing, false);
        if (!create) return (GetInbox(Inbox.DefaultId)!, false);
        return CreateInbox(key!.Trim());
    }

    public Inbox? GetInbox(string slug) => QueryInbox("slug = $k", slug);

    public Inbox? GetInbox(long id) => QueryInbox("id = $k", id);

    public List<InboxSummary> ListInboxes()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT i.id, i.slug, i.name, i.created, i.purge,
                   COUNT(m.id), COALESCE(SUM(1 - m.is_read), 0), COALESCE(SUM(m.size), 0)
            FROM inboxes i LEFT JOIN messages m ON m.inbox_id = i.id
            GROUP BY i.id
            ORDER BY i.id <> 1, lower(i.name)
            """;
        var list = new List<InboxSummary>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var id = r.GetInt64(0);
            list.Add(new InboxSummary(id, r.GetString(1), r.GetString(2), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)),
                id == Inbox.DefaultId, ParsePurge(r.IsDBNull(4) ? null : r.GetString(4)), r.GetInt32(5), r.GetInt32(6), r.GetInt64(7)));
        }
        return list;
    }

    /// <summary>Creates an inbox named <paramref name="name"/>, or returns the existing one with the same slug.</summary>
    public (Inbox Inbox, bool Created) CreateInbox(string name)
    {
        var slug = InboxSlug.Normalize(name) ?? throw new ArgumentException("An inbox name needs at least one letter or digit.", nameof(name));
        name = name.Trim();
        if (name.Length > 80) name = name[..80];
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO inboxes (slug, name, created) VALUES ($slug, $name, $now)";
        cmd.Parameters.AddWithValue("$slug", slug);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$now", _time.GetUtcNow().ToUnixTimeMilliseconds());
        var created = cmd.ExecuteNonQuery() > 0;
        return (GetInbox(slug)!, created);
    }

    /// <summary>Renames an inbox and sets (or clears, with null) its own purge rules.</summary>
    public Inbox? UpdateInbox(long id, string name, PurgeSettings? purge)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("Name is required.", nameof(name));
        if (name.Length > 80) name = name[..80];
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE inboxes SET name = $name, purge = $purge WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$purge", purge is null ? DBNull.Value : JsonSerializer.Serialize(purge, Json));
        return cmd.ExecuteNonQuery() == 0 ? null : GetInbox(id);
    }

    /// <summary>Deletes an inbox and all of its mail. The Default inbox can't be deleted. Returns messages removed, or null if not found.</summary>
    public int? DeleteInbox(long id)
    {
        if (id == Inbox.DefaultId) throw new InvalidOperationException("The Default inbox can't be deleted.");
        if (GetInbox(id) is null) return null;
        var removed = DeleteAll(id);
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM inboxes WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        return removed;
    }

    // ------------------------------------------------------------------ settings

    public PurgeSettings GetPurgeSettings()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = 'purge'";
        return ParsePurge(cmd.ExecuteScalar() as string) ?? new PurgeSettings();
    }

    public void SavePurgeSettings(PurgeSettings s)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO settings (key, value) VALUES ('purge', $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$v", JsonSerializer.Serialize(s, Json));
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ helpers

    private static PurgeSettings? ParsePurge(string? json)
    {
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<PurgeSettings>(json, Json); }
        catch (JsonException) { return null; }
    }

    private Inbox? QueryInbox(string where, object key)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT id, slug, name, created, purge FROM inboxes WHERE {where}";
        cmd.Parameters.AddWithValue("$k", key);
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new Inbox(r.GetInt64(0), r.GetString(1), r.GetString(2), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)), ParsePurge(r.IsDBNull(4) ? null : r.GetString(4)))
            : null;
    }

    private static MessageSummary ReadSummary(SqliteDataReader r) => new(
        r.GetString(0), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)), r.GetString(2), r.GetString(3),
        r.GetString(4), r.GetString(5), r.GetInt64(6), r.GetInt32(7), r.GetInt32(8) != 0,
        r.IsDBNull(9) ? Inbox.DefaultSlug : r.GetString(9));

    private int DeleteIds(IEnumerable<string> ids)
    {
        var count = 0;
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM messages WHERE id = $id";
        var p = cmd.Parameters.Add("$id", SqliteType.Text);
        var removed = new List<string>();
        foreach (var id in ids)
        {
            p.Value = id;
            if (cmd.ExecuteNonQuery() > 0) { count++; removed.Add(id); }
        }
        tx.Commit();

        foreach (var id in removed)
        {
            try { File.Delete(RawPath(id)); }
            catch (IOException ex) { _log.LogWarning(ex, "Could not delete {File}", RawPath(id)); }
            catch (UnauthorizedAccessException ex) { _log.LogWarning(ex, "Could not delete {File}", RawPath(id)); }
        }
        return count;
    }

    private static List<string> ReadIds(SqliteConnection db, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            if (sql.Contains(name, StringComparison.Ordinal)) cmd.Parameters.AddWithValue(name, value);
        var ids = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetString(0));
        return ids;
    }

    private static bool ColumnExists(SqliteConnection db, string table, string column)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $c";
        cmd.Parameters.AddWithValue("$c", column);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }

    private static void Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>IDs are generated GUIDs; refuse anything else so a crafted id can't escape the message folder.</summary>
    private static string SafeId(string id) =>
        id.Length == 32 && id.All(Uri.IsHexDigit) ? id : throw new ArgumentException("Invalid message id.", nameof(id));
}
