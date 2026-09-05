// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace NPS.Daemon.Npsd.Inbox;

/// <summary>
/// Durable SQLite per-NID inbox with process-local long-poll notification.
/// Undelivered rows, priority order, expiry and acknowledgements survive daemon
/// restart when the same database path is reused.
/// </summary>
public sealed class InboxStore : IDisposable
{
    private readonly string _connectionString;
    private readonly Func<DateTimeOffset> _now;
    private readonly SqliteConnection? _keepAlive;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _signals = new();

    /// <summary>Open a file-backed inbox store.</summary>
    public InboxStore(string sqlitePath)
        : this(FileConnectionString(sqlitePath), static () => DateTimeOffset.UtcNow, keepAlive: null)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(sqlitePath));
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);
        EnsureSchema();
    }

    private InboxStore(
        string connectionString,
        Func<DateTimeOffset> now,
        SqliteConnection? keepAlive)
    {
        _connectionString = connectionString;
        _now = now;
        _keepAlive = keepAlive;
    }

    /// <summary>Create a private in-memory SQLite store for tests.</summary>
    internal static InboxStore CreateInMemoryForTests(Func<DateTimeOffset>? now = null)
    {
        var connectionString =
            $"Data Source=file:npsd-inbox-{Guid.NewGuid():N}?mode=memory&cache=shared;Default Timeout=5";
        var keepAlive = new SqliteConnection(connectionString);
        keepAlive.Open();
        var store = new InboxStore(
            connectionString,
            now ?? (static () => DateTimeOffset.UtcNow),
            keepAlive);
        store.EnsureSchema();
        return store;
    }

    /// <summary>Open a file-backed store with an injectable clock for deterministic tests.</summary>
    internal static InboxStore CreateFileForTests(string sqlitePath, Func<DateTimeOffset> now)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(sqlitePath));
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);
        var store = new InboxStore(FileConnectionString(sqlitePath), now, keepAlive: null);
        store.EnsureSchema();
        return store;
    }

    /// <summary>Append a message atomically after expiry purge and depth enforcement.</summary>
    /// <exception cref="InboxFullException">If the inbox is at <paramref name="maxDepth"/>.</exception>
    public ulong Enqueue(
        string nid,
        byte[] payload,
        string contentType,
        int priority,
        TimeSpan ttl,
        int maxDepth)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nid);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), "Inbox TTL must be positive.");
        if (maxDepth <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxDepth), "Inbox depth must be positive.");

        var now = _now();
        var nowMs = now.ToUnixTimeMilliseconds();
        var expiresMs = now.Add(ttl).ToUnixTimeMilliseconds();
        long messageId;

        using (var connection = Open())
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            PurgeExpired(connection, transaction, nid, nowMs);

            using (var count = connection.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM inbox_messages WHERE nid = $nid;";
                count.Parameters.AddWithValue("$nid", nid);
                if ((long)(count.ExecuteScalar() ?? 0L) >= maxDepth)
                    throw new InboxFullException(nid, maxDepth);
            }

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO inbox_messages
                      (nid, enqueued_at_ms, expires_at_ms, priority, payload, content_type)
                    VALUES
                      ($nid, $enqueued, $expires, $priority, $payload, $content_type);
                    SELECT last_insert_rowid();
                    """;
                insert.Parameters.AddWithValue("$nid", nid);
                insert.Parameters.AddWithValue("$enqueued", nowMs);
                insert.Parameters.AddWithValue("$expires", expiresMs);
                insert.Parameters.AddWithValue("$priority", priority);
                insert.Parameters.Add("$payload", SqliteType.Blob).Value = payload;
                insert.Parameters.AddWithValue("$content_type", contentType);
                messageId = (long)(insert.ExecuteScalar()
                    ?? throw new InvalidOperationException("SQLite did not assign an inbox message ID."));
            }

            transaction.Commit();
        }

        Notify(nid);
        return checked((ulong)messageId);
    }

    /// <summary>
    /// Returns up to <paramref name="batchSize"/> pending messages without removing
    /// them. Waits for a process-local enqueue notification when currently empty.
    /// Callers acknowledge completed delivery with <see cref="Ack"/>.
    /// </summary>
    public async Task<IReadOnlyList<InboxMessage>> PeekAsync(
        string nid,
        int batchSize,
        TimeSpan wait,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nid);
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize));

        var snapshot = SnapshotNonExpired(nid, batchSize);
        if (snapshot.Count > 0 || wait <= TimeSpan.Zero)
            return snapshot;

        var signal = GetSignal(nid);
        while (signal.Wait(0))
        {
            // Remove notifications already represented by the empty snapshot.
        }

        // Close the snapshot/signal race: an enqueue after the first snapshot is
        // visible here even if its notification was consumed above.
        snapshot = SnapshotNonExpired(nid, batchSize);
        if (snapshot.Count > 0)
            return snapshot;

        await signal.WaitAsync(wait, ct).ConfigureAwait(false);
        return SnapshotNonExpired(nid, batchSize);
    }

    /// <summary>Durably removes a message. False means already acknowledged or unknown.</summary>
    public bool Ack(string nid, ulong messageId)
    {
        if (messageId > long.MaxValue)
            return false;

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM inbox_messages WHERE nid = $nid AND message_id = $message_id;";
        command.Parameters.AddWithValue("$nid", nid);
        command.Parameters.AddWithValue("$message_id", checked((long)messageId));
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>Current number of non-expired messages for the given NID.</summary>
    public int Depth(string nid)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        PurgeExpired(connection, transaction, nid, _now().ToUnixTimeMilliseconds());
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM inbox_messages WHERE nid = $nid;";
        command.Parameters.AddWithValue("$nid", nid);
        var count = checked((int)(long)(command.ExecuteScalar() ?? 0L));
        transaction.Commit();
        return count;
    }

    /// <summary>Throws when the durable store cannot be queried.</summary>
    public void Ping()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM inbox_messages LIMIT 1;";
        command.ExecuteScalar();
    }

    public void Dispose()
    {
        foreach (var signal in _signals.Values)
            signal.Dispose();
        _keepAlive?.Dispose();
    }

    private IReadOnlyList<InboxMessage> SnapshotNonExpired(string nid, int batchSize)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        PurgeExpired(connection, transaction, nid, _now().ToUnixTimeMilliseconds());

        var messages = new List<InboxMessage>(batchSize);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT message_id, nid, enqueued_at_ms, expires_at_ms,
                       priority, payload, content_type
                FROM inbox_messages
                WHERE nid = $nid
                ORDER BY priority DESC, enqueued_at_ms ASC, message_id ASC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$nid", nid);
            command.Parameters.AddWithValue("$limit", batchSize);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                messages.Add(new InboxMessage
                {
                    MessageId = checked((ulong)reader.GetInt64(0)),
                    Nid = reader.GetString(1),
                    EnqueuedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                    ExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                    Priority = reader.GetInt32(4),
                    Payload = (byte[])reader[5],
                    ContentType = reader.GetString(6),
                });
            }
        }

        transaction.Commit();
        return messages;
    }

    private SemaphoreSlim GetSignal(string nid) =>
        _signals.GetOrAdd(nid, static _ => new SemaphoreSlim(0, 1));

    private void Notify(string nid)
    {
        try
        {
            GetSignal(nid).Release();
        }
        catch (SemaphoreFullException)
        {
            // A pending notification already represents all durable rows.
        }
    }

    private static void PurgeExpired(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string nid,
        long nowMs)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM inbox_messages WHERE nid = $nid AND expires_at_ms <= $now;";
        command.Parameters.AddWithValue("$nid", nid);
        command.Parameters.AddWithValue("$now", nowMs);
        command.ExecuteNonQuery();
    }

    private void EnsureSchema()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS inbox_messages (
              message_id     INTEGER PRIMARY KEY AUTOINCREMENT,
              nid            TEXT NOT NULL,
              enqueued_at_ms INTEGER NOT NULL,
              expires_at_ms  INTEGER NOT NULL,
              priority       INTEGER NOT NULL,
              payload        BLOB NOT NULL,
              content_type   TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_inbox_delivery
              ON inbox_messages(nid, priority DESC, enqueued_at_ms ASC, message_id ASC);
            CREATE INDEX IF NOT EXISTS idx_inbox_expiry
              ON inbox_messages(expires_at_ms);
            PRAGMA user_version = 1;
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static string FileConnectionString(string sqlitePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlitePath);
        return new SqliteConnectionStringBuilder
        {
            DataSource = sqlitePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 5,
        }.ToString();
    }
}

/// <summary>Thrown when enqueueing into an inbox at its configured capacity.</summary>
public sealed class InboxFullException(string nid, int maxDepth) :
    Exception($"Inbox is full for {nid} (max depth {maxDepth})")
{
    public string Nid { get; } = nid;
    public int MaxDepth { get; } = maxDepth;
}
