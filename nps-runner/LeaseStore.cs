// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace NPS.Daemon.Runner;

/// <summary>Outcome of a <see cref="LeaseStore.TryClaim"/> attempt (NPS-CR-0007 §4.2).</summary>
internal enum ClaimResult
{
    /// <summary>Lease granted on a previously-unleased task or renewed by this owner.</summary>
    Granted,
    /// <summary>A live lease is held by a different owner — caller backs off without ack.</summary>
    Conflict,
    /// <summary>An expired lease was atomically transferred to this owner.</summary>
    Reclaimed,
    /// <summary>This single-worker task already has a durable terminal record.</summary>
    AlreadyCompleted,
}

internal sealed record ClaimOutcome(ClaimResult Result, string? ErrorCode = null);

/// <summary>
/// Durable SQLite task-claim store for NPS-CR-0007 §4. Separate daemon processes
/// coordinate through the same database file. Each store instance carries a unique,
/// process-lifetime owner token so a restarted or stale process cannot renew, release,
/// or complete a lease owned by another instance, even when both use the same runner NID.
/// </summary>
internal sealed class LeaseStore : IDisposable
{
    /// <summary>NPS-CR-0007 §7: returned when another owner holds a live lease.</summary>
    public const string ClaimConflictCode = "NOP-CLAIM-CONFLICT";

    /// <summary>Lease duration clamp bounds (NPS-CR-0007 §4.1).</summary>
    public const int MinLeaseSeconds = 10;
    public const int MaxLeaseSeconds = 600;

    private readonly string _connectionString;
    private readonly string _ownerInstanceId;
    private readonly Func<DateTimeOffset> _now;
    private readonly SqliteConnection? _keepAlive;

    /// <summary>Open a file-backed store shared by runner processes.</summary>
    public LeaseStore(
        string sqlitePath,
        string ownerInstanceId,
        Func<DateTimeOffset>? now = null)
        : this(
            FileConnectionString(sqlitePath),
            ownerInstanceId,
            now ?? (static () => DateTimeOffset.UtcNow),
            keepAlive: null)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(sqlitePath));
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);
        EnsureSchema();
    }

    private LeaseStore(
        string connectionString,
        string ownerInstanceId,
        Func<DateTimeOffset> now,
        SqliteConnection? keepAlive)
    {
        if (string.IsNullOrWhiteSpace(ownerInstanceId))
            throw new ArgumentException("Owner instance ID must not be empty.", nameof(ownerInstanceId));

        _connectionString = connectionString;
        _ownerInstanceId = ownerInstanceId;
        _now = now;
        _keepAlive = keepAlive;
    }

    /// <summary>Create a private in-memory store for unit tests.</summary>
    internal static LeaseStore CreateInMemoryForTests(
        Func<DateTimeOffset>? now = null,
        string? ownerInstanceId = null)
        => CreateSharedInMemoryForTests(
            $"nps-runner-{Guid.NewGuid():N}",
            ownerInstanceId ?? Guid.NewGuid().ToString("N"),
            now);

    /// <summary>Create one owner view over a named shared in-memory test database.</summary>
    internal static LeaseStore CreateSharedInMemoryForTests(
        string databaseName,
        string ownerInstanceId,
        Func<DateTimeOffset>? now = null)
    {
        var connectionString =
            $"Data Source=file:{databaseName}?mode=memory&cache=shared;Default Timeout=5";
        var keepAlive = new SqliteConnection(connectionString);
        keepAlive.Open();
        var store = new LeaseStore(
            connectionString,
            ownerInstanceId,
            now ?? (static () => DateTimeOffset.UtcNow),
            keepAlive);
        store.EnsureSchema();
        return store;
    }

    public void Dispose() => _keepAlive?.Dispose();

    /// <summary>
    /// Atomically claim a task. The duration is clamped to [10, 600] seconds.
    /// A same-owner/same-dedup claim renews; a live different owner conflicts;
    /// an expired lease is reclaimed. A durable terminal record for this
    /// single-worker task returns <see cref="ClaimResult.AlreadyCompleted"/>.
    /// </summary>
    public ClaimOutcome TryClaim(
        string taskId,
        string runnerNid,
        int leaseSeconds,
        string dedupKey)
    {
        ValidateIdentifiers(taskId, runnerNid, dedupKey);
        var nowMs = _now().ToUnixTimeMilliseconds();
        var expiryMs = checked(nowMs + Math.Clamp(
            leaseSeconds,
            MinLeaseSeconds,
            MaxLeaseSeconds) * 1000L);

        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);

        if (HasTerminal(connection, transaction, dedupKey, taskId))
        {
            transaction.Commit();
            return new ClaimOutcome(ClaimResult.AlreadyCompleted);
        }

        var existing = ReadLease(connection, transaction, taskId);
        if (existing is not null && existing.ExpiryMs > nowMs)
        {
            if (!string.Equals(existing.RunnerNid, runnerNid, StringComparison.Ordinal) ||
                !string.Equals(existing.OwnerInstanceId, _ownerInstanceId, StringComparison.Ordinal) ||
                !string.Equals(existing.DedupKey, dedupKey, StringComparison.Ordinal))
            {
                transaction.Commit();
                return new ClaimOutcome(ClaimResult.Conflict, ClaimConflictCode);
            }

            UpdateLease(connection, transaction, taskId, runnerNid, dedupKey, expiryMs);
            transaction.Commit();
            return new ClaimOutcome(ClaimResult.Granted);
        }

        if (existing is null)
            InsertLease(connection, transaction, taskId, runnerNid, dedupKey, expiryMs);
        else
            UpdateLease(connection, transaction, taskId, runnerNid, dedupKey, expiryMs);

        transaction.Commit();
        return new ClaimOutcome(existing is null ? ClaimResult.Granted : ClaimResult.Reclaimed);
    }

    /// <summary>Extend a live lease only when this process instance still owns it.</summary>
    public bool Renew(string taskId, string runnerNid, int leaseSeconds)
    {
        var nowMs = _now().ToUnixTimeMilliseconds();
        var expiryMs = checked(nowMs + Math.Clamp(
            leaseSeconds,
            MinLeaseSeconds,
            MaxLeaseSeconds) * 1000L);

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE runner_leases
            SET lease_expiry_ms = $expiry
            WHERE task_id = $task
              AND runner_nid = $runner
              AND owner_instance_id = $owner
              AND lease_expiry_ms > $now
            """;
        command.Parameters.AddWithValue("$expiry", expiryMs);
        command.Parameters.AddWithValue("$task", taskId);
        command.Parameters.AddWithValue("$runner", runnerNid);
        command.Parameters.AddWithValue("$owner", _ownerInstanceId);
        command.Parameters.AddWithValue("$now", nowMs);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>Release a lease only when this process instance owns it.</summary>
    public bool Release(string taskId, string runnerNid)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM runner_leases
            WHERE task_id = $task
              AND runner_nid = $runner
              AND owner_instance_id = $owner
            """;
        command.Parameters.AddWithValue("$task", taskId);
        command.Parameters.AddWithValue("$runner", runnerNid);
        command.Parameters.AddWithValue("$owner", _ownerInstanceId);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>
    /// Durably record a terminal DAG node while retaining the task lease. The write
    /// succeeds only for the live owner, preventing a stale process from reporting.
    /// </summary>
    public bool TryRecordTerminal(
        string taskId,
        string runnerNid,
        string dedupKey,
        string nodeId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (!OwnsLiveLease(connection, transaction, taskId, runnerNid, dedupKey))
        {
            transaction.Commit();
            return false;
        }

        InsertTerminal(connection, transaction, taskId, dedupKey, nodeId);
        transaction.Commit();
        return true;
    }

    /// <summary>
    /// Atomically record terminal completion for the current single-worker task and
    /// release its lease. The commit is rejected if ownership was lost or expired.
    /// </summary>
    public bool TryCompleteTask(
        string taskId,
        string runnerNid,
        string dedupKey)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (!OwnsLiveLease(connection, transaction, taskId, runnerNid, dedupKey))
        {
            transaction.Commit();
            return false;
        }

        InsertTerminal(connection, transaction, taskId, dedupKey, taskId);
        DeleteOwnedLease(connection, transaction, taskId, runnerNid);
        transaction.Commit();
        return true;
    }

    /// <summary>True when durable state records this node terminal under the dedup key.</summary>
    public bool IsNodeDone(string dedupKey, string nodeId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM runner_terminal_nodes
            WHERE dedup_key = $dedup AND node_id = $node
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$dedup", dedupKey);
        command.Parameters.AddWithValue("$node", nodeId);
        return command.ExecuteScalar() is not null;
    }

    private bool OwnsLiveLease(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId,
        string runnerNid,
        string dedupKey)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1
            FROM runner_leases
            WHERE task_id = $task
              AND runner_nid = $runner
              AND owner_instance_id = $owner
              AND dedup_key = $dedup
              AND lease_expiry_ms > $now
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$task", taskId);
        command.Parameters.AddWithValue("$runner", runnerNid);
        command.Parameters.AddWithValue("$owner", _ownerInstanceId);
        command.Parameters.AddWithValue("$dedup", dedupKey);
        command.Parameters.AddWithValue("$now", _now().ToUnixTimeMilliseconds());
        return command.ExecuteScalar() is not null;
    }

    private static bool HasTerminal(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string dedupKey,
        string nodeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1
            FROM runner_terminal_nodes
            WHERE dedup_key = $dedup AND node_id = $node
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$dedup", dedupKey);
        command.Parameters.AddWithValue("$node", nodeId);
        return command.ExecuteScalar() is not null;
    }

    private static LeaseRow? ReadLease(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT runner_nid, owner_instance_id, dedup_key, lease_expiry_ms
            FROM runner_leases
            WHERE task_id = $task
            """;
        command.Parameters.AddWithValue("$task", taskId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new LeaseRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3))
            : null;
    }

    private void InsertLease(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId,
        string runnerNid,
        string dedupKey,
        long expiryMs)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO runner_leases
              (task_id, runner_nid, owner_instance_id, dedup_key, lease_expiry_ms, generation)
            VALUES
              ($task, $runner, $owner, $dedup, $expiry, 1)
            """;
        BindLease(command, taskId, runnerNid, dedupKey, expiryMs);
        command.ExecuteNonQuery();
    }

    private void UpdateLease(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId,
        string runnerNid,
        string dedupKey,
        long expiryMs)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE runner_leases
            SET runner_nid = $runner,
                owner_instance_id = $owner,
                dedup_key = $dedup,
                lease_expiry_ms = $expiry,
                generation = generation + 1
            WHERE task_id = $task
            """;
        BindLease(command, taskId, runnerNid, dedupKey, expiryMs);
        command.ExecuteNonQuery();
    }

    private void BindLease(
        SqliteCommand command,
        string taskId,
        string runnerNid,
        string dedupKey,
        long expiryMs)
    {
        command.Parameters.AddWithValue("$task", taskId);
        command.Parameters.AddWithValue("$runner", runnerNid);
        command.Parameters.AddWithValue("$owner", _ownerInstanceId);
        command.Parameters.AddWithValue("$dedup", dedupKey);
        command.Parameters.AddWithValue("$expiry", expiryMs);
    }

    private void InsertTerminal(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId,
        string dedupKey,
        string nodeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO runner_terminal_nodes
              (dedup_key, node_id, task_id, completed_at_ms, owner_instance_id)
            VALUES
              ($dedup, $node, $task, $completed, $owner)
            """;
        command.Parameters.AddWithValue("$dedup", dedupKey);
        command.Parameters.AddWithValue("$node", nodeId);
        command.Parameters.AddWithValue("$task", taskId);
        command.Parameters.AddWithValue("$completed", _now().ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$owner", _ownerInstanceId);
        command.ExecuteNonQuery();
    }

    private void DeleteOwnedLease(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId,
        string runnerNid)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM runner_leases
            WHERE task_id = $task
              AND runner_nid = $runner
              AND owner_instance_id = $owner
            """;
        command.Parameters.AddWithValue("$task", taskId);
        command.Parameters.AddWithValue("$runner", runnerNid);
        command.Parameters.AddWithValue("$owner", _ownerInstanceId);
        command.ExecuteNonQuery();
    }

    private void EnsureSchema()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS runner_leases (
              task_id           TEXT PRIMARY KEY,
              runner_nid        TEXT NOT NULL,
              owner_instance_id TEXT NOT NULL,
              dedup_key         TEXT NOT NULL,
              lease_expiry_ms   INTEGER NOT NULL,
              generation        INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_runner_leases_expiry
              ON runner_leases(lease_expiry_ms);
            CREATE TABLE IF NOT EXISTS runner_terminal_nodes (
              dedup_key         TEXT NOT NULL,
              node_id           TEXT NOT NULL,
              task_id           TEXT NOT NULL,
              completed_at_ms   INTEGER NOT NULL,
              owner_instance_id TEXT NOT NULL,
              PRIMARY KEY (dedup_key, node_id)
            );
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
        if (string.IsNullOrWhiteSpace(sqlitePath))
            throw new ArgumentException("SQLite path must not be empty.", nameof(sqlitePath));
        return new SqliteConnectionStringBuilder
        {
            DataSource = sqlitePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 5,
        }.ToString();
    }

    private static void ValidateIdentifiers(
        string taskId,
        string runnerNid,
        string dedupKey)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            throw new ArgumentException("Task ID must not be empty.", nameof(taskId));
        if (string.IsNullOrWhiteSpace(runnerNid))
            throw new ArgumentException("Runner NID must not be empty.", nameof(runnerNid));
        if (string.IsNullOrWhiteSpace(dedupKey))
            throw new ArgumentException("Dedup key must not be empty.", nameof(dedupKey));
    }

    private sealed record LeaseRow(
        string RunnerNid,
        string OwnerInstanceId,
        string DedupKey,
        long ExpiryMs);

    /// <summary>
    /// dedup_key = sha256(task_id ‖ dag_hash), lowercase hex (NPS-CR-0007 §4.1).
    /// A NUL byte is the unambiguous separator between the two components.
    /// </summary>
    public static string ComputeDedupKey(string taskId, string dagHash)
    {
        var buffer = Encoding.UTF8.GetBytes(taskId)
            .Concat(new byte[] { 0 })
            .Concat(Encoding.UTF8.GetBytes(dagHash))
            .ToArray();
        return Convert.ToHexString(SHA256.HashData(buffer)).ToLowerInvariant();
    }
}
