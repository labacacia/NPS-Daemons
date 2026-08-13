// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.Data.Sqlite;
using NPS.NDP.Frames;
using NPS.NDP.Registry;

namespace NPS.Daemon.Registry;

/// <summary>
/// SQLite-backed implementation of <see cref="INdpRegistry"/> for nps-registry.
///
/// <para>TTL expiry is evaluated lazily on every read (no background timer).
/// Expired entries are purged during <see cref="GetAll"/> and <see cref="Resolve"/> calls.</para>
///
/// <para>Schema:
/// <code>
/// CREATE TABLE announcements (
///   nid            TEXT PRIMARY KEY,
///   addresses_json TEXT NOT NULL,
///   caps_json      TEXT NOT NULL,
///   node_type      TEXT,
///   ttl            INTEGER NOT NULL,
///   timestamp      TEXT NOT NULL,
///   signature      TEXT NOT NULL,
///   expires_at     TEXT NOT NULL,  -- ISO-8601 UTC; used for lazy eviction
///   cluster_anchor TEXT,           -- NPS-CR-0009
///   cluster_epoch  INTEGER NOT NULL DEFAULT 1
/// );
/// CREATE TABLE graph_meta (
///   id  INTEGER PRIMARY KEY CHECK (id = 1),
///   seq INTEGER NOT NULL DEFAULT 0
/// );
/// -- NPS-CR-0009 / NDP §9: the propagated (cluster_anchor, cluster_epoch, active_nid)
/// -- tuple. Monotonic per cluster: never downgraded to a lower epoch.
/// CREATE TABLE cluster_ownership (
///   cluster_anchor TEXT PRIMARY KEY,
///   cluster_epoch  INTEGER NOT NULL,
///   active_nid     TEXT NOT NULL,
///   source         TEXT NOT NULL,   -- "local" or the peer registry NID it arrived from
///   updated_at     TEXT NOT NULL
/// );
/// </code>
/// </para>
/// </summary>
public sealed class SqliteNdpRegistry : INdpRegistry, IDisposable
{
    private readonly string            _connStr;
    private readonly SqliteConnection? _keepAlive;

    private static readonly JsonSerializerOptions _json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public SqliteNdpRegistry(string sqlitePath)
    {
        _connStr = $"Data Source={sqlitePath};Cache=Shared";
        EnsureSchema();
    }

    public static SqliteNdpRegistry CreateInMemory()
    {
        var name      = $"ndp-registry-{Guid.NewGuid():N}";
        var connStr   = $"Data Source=file:{name}?mode=memory&cache=shared";
        var keepAlive = new SqliteConnection(connStr);
        keepAlive.Open();
        return new SqliteNdpRegistry(connStr, keepAlive);
    }

    private SqliteNdpRegistry(string connStr, SqliteConnection keepAlive)
    {
        _connStr   = connStr;
        _keepAlive = keepAlive;
        EnsureSchema();
    }

    public void Dispose() => _keepAlive?.Dispose();

    // ── INdpRegistry ──────────────────────────────────────────────────────────

    public void Announce(AnnounceFrame frame)
    {
        using var conn = Open();
        if (frame.Ttl == 0)
        {
            using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM announcements WHERE nid = $nid";
            del.Parameters.AddWithValue("$nid", frame.Nid);
            del.ExecuteNonQuery();
            BumpSeq(conn);
            return;
        }

        var expiresAt = (DateTime.TryParse(frame.Timestamp, out var ts) ? ts : DateTime.UtcNow)
                        .AddSeconds(frame.Ttl).ToString("O");

        using var upsert = conn.CreateCommand();
        upsert.CommandText = """
            INSERT INTO announcements
              (nid, addresses_json, caps_json, node_type, ttl, timestamp, signature, expires_at,
               cluster_anchor, cluster_epoch)
            VALUES
              ($nid, $addr, $caps, $nt, $ttl, $ts, $sig, $exp, $ca, $ce)
            ON CONFLICT(nid) DO UPDATE SET
              addresses_json = excluded.addresses_json,
              caps_json      = excluded.caps_json,
              node_type      = excluded.node_type,
              ttl            = excluded.ttl,
              timestamp      = excluded.timestamp,
              signature      = excluded.signature,
              expires_at     = excluded.expires_at,
              cluster_anchor = excluded.cluster_anchor,
              -- NPS-CR-0009: cluster_epoch is monotonic per Anchor. A re-announce of the
              -- same NID within the same cluster carrying a lower epoch is stale (replay /
              -- superseded leader) and MUST NOT downgrade the stored fence.
              cluster_epoch  = CASE
                                 WHEN announcements.cluster_anchor IS excluded.cluster_anchor
                                   THEN MAX(announcements.cluster_epoch, excluded.cluster_epoch)
                                 ELSE excluded.cluster_epoch
                               END
            """;
        upsert.Parameters.AddWithValue("$nid",  frame.Nid);
        upsert.Parameters.AddWithValue("$addr", JsonSerializer.Serialize(frame.Addresses, _json));
        upsert.Parameters.AddWithValue("$caps", JsonSerializer.Serialize(frame.Capabilities, _json));
        upsert.Parameters.AddWithValue("$nt",   (object?)frame.NodeType ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$ttl",  (long)frame.Ttl);
        upsert.Parameters.AddWithValue("$ts",   frame.Timestamp);
        upsert.Parameters.AddWithValue("$sig",  frame.Signature);
        upsert.Parameters.AddWithValue("$exp",  expiresAt);
        upsert.Parameters.AddWithValue("$ca",   (object?)frame.ClusterAnchor ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$ce",   (long)(frame.ClusterEpoch ?? DefaultClusterEpoch));
        upsert.ExecuteNonQuery();

        BumpSeq(conn);

        // NPS-CR-0009 / NDP §9: keep the propagated cluster tuple up to date from local
        // announcements. Never throws — split-brain is surfaced at resolve time.
        if (!string.IsNullOrEmpty(frame.ClusterAnchor))
        {
            var stored = ReadStoredEpoch(conn, frame.Nid) ?? frame.ClusterEpoch ?? DefaultClusterEpoch;
            ApplyClusterOwnership(conn, frame.ClusterAnchor, stored, frame.Nid, source: LocalSource);
        }
    }

    // ── NPS-CR-0009 multi-Anchor HA ───────────────────────────────────────────

    /// <summary>Epoch assumed for an AnnounceFrame that carries no <c>cluster_epoch</c> (NDP §3.1).</summary>
    public const ulong DefaultClusterEpoch = 1UL;

    /// <summary>Ownership <c>source</c> marker for tuples learned from local announcements.</summary>
    public const string LocalSource = "local";

    /// <summary>
    /// Resolves a <c>cluster_anchor</c> NID to the cluster's current <b>active</b> Anchor:
    /// the live member announcement with the highest <c>cluster_epoch</c> (absent ⇒ 1),
    /// per NDP §9 / NPS-CR-0009 §3.4.
    /// </summary>
    /// <returns>The active Anchor's announcement, or <c>null</c> when no live member exists.</returns>
    /// <exception cref="NdpClusterSplitException">
    /// More than one live member advertises the top epoch — a split-brain fault that MUST be
    /// reported as <c>NDP-CLUSTER-SPLIT</c> rather than resolved arbitrarily.
    /// </exception>
    public AnnounceFrame? ResolveCluster(string clusterAnchor)
    {
        ArgumentException.ThrowIfNullOrEmpty(clusterAnchor);

        using var conn = Open();
        Purge(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT nid, addresses_json, caps_json, node_type, ttl, timestamp, signature,
                   cluster_anchor, cluster_epoch
            FROM   announcements
            WHERE  cluster_anchor = $ca
            """;
        cmd.Parameters.AddWithValue("$ca", clusterAnchor);

        var members = new List<AnnounceFrame>();
        using (var rdr = cmd.ExecuteReader())
            while (rdr.Read()) members.Add(MapRow(rdr));

        if (members.Count == 0) return null;

        var top     = members.Max(f => f.ClusterEpoch ?? DefaultClusterEpoch);
        var leaders = members.Where(f => (f.ClusterEpoch ?? DefaultClusterEpoch) == top).ToList();
        if (leaders.Count > 1)
            throw new NdpClusterSplitException(clusterAnchor, top);

        return leaders[0];
    }

    /// <summary>
    /// The propagated <c>(cluster_anchor, cluster_epoch, active_nid)</c> tuple this registry
    /// currently holds for <paramref name="clusterAnchor"/>, or <c>null</c> if unknown.
    /// </summary>
    public ClusterOwnershipEntry? GetClusterOwnership(string clusterAnchor)
    {
        ArgumentException.ThrowIfNullOrEmpty(clusterAnchor);
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT cluster_anchor, cluster_epoch, active_nid, source, updated_at
            FROM   cluster_ownership
            WHERE  cluster_anchor = $ca
            """;
        cmd.Parameters.AddWithValue("$ca", clusterAnchor);
        using var rdr = cmd.ExecuteReader();
        return rdr.Read() ? MapOwnership(rdr) : null;
    }

    /// <summary>All cluster tuples this registry knows — the set it propagates to federated peers.</summary>
    public IReadOnlyList<ClusterOwnershipEntry> GetAllClusterOwnership()
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT cluster_anchor, cluster_epoch, active_nid, source, updated_at
            FROM   cluster_ownership
            ORDER BY cluster_anchor
            """;
        using var rdr = cmd.ExecuteReader();
        var list = new List<ClusterOwnershipEntry>();
        while (rdr.Read()) list.Add(MapOwnership(rdr));
        return list;
    }

    /// <summary>
    /// Applies a <c>(cluster_anchor, cluster_epoch, active_nid)</c> tuple received from a
    /// federated peer (NDP §9). A strictly higher epoch is preferred; an equal or lower epoch
    /// is ignored (monotonic per cluster — a cluster is never downgraded). An equal epoch
    /// naming a <i>different</i> active Anchor is a cross-registry split-brain and is reported
    /// as <see cref="ClusterOwnershipOutcome.Split"/> without mutating state.
    /// </summary>
    public ClusterOwnershipOutcome ApplyClusterOwnership(
        string clusterAnchor, ulong clusterEpoch, string activeNid, string source)
    {
        ArgumentException.ThrowIfNullOrEmpty(clusterAnchor);
        ArgumentException.ThrowIfNullOrEmpty(activeNid);
        ArgumentException.ThrowIfNullOrEmpty(source);

        using var conn = Open();
        return ApplyClusterOwnership(conn, clusterAnchor, clusterEpoch, activeNid, source);
    }

    private static ClusterOwnershipOutcome ApplyClusterOwnership(
        SqliteConnection conn, string clusterAnchor, ulong clusterEpoch, string activeNid, string source)
    {
        using var sel = conn.CreateCommand();
        sel.CommandText = "SELECT cluster_epoch, active_nid FROM cluster_ownership WHERE cluster_anchor = $ca";
        sel.Parameters.AddWithValue("$ca", clusterAnchor);

        ulong?  curEpoch = null;
        string? curNid   = null;
        using (var rdr = sel.ExecuteReader())
            if (rdr.Read())
            {
                curEpoch = (ulong)rdr.GetInt64(0);
                curNid   = rdr.GetString(1);
            }

        if (curEpoch is not null)
        {
            if (clusterEpoch < curEpoch.Value)
                return ClusterOwnershipOutcome.Ignored;                        // stale — never downgrade

            if (clusterEpoch == curEpoch.Value)
                return string.Equals(curNid, activeNid, StringComparison.Ordinal)
                    ? ClusterOwnershipOutcome.Ignored                          // idempotent re-announce
                    : ClusterOwnershipOutcome.Split;                           // NDP-CLUSTER-SPLIT
        }

        using var up = conn.CreateCommand();
        up.CommandText = """
            INSERT INTO cluster_ownership (cluster_anchor, cluster_epoch, active_nid, source, updated_at)
            VALUES ($ca, $ce, $nid, $src, $now)
            ON CONFLICT(cluster_anchor) DO UPDATE SET
              cluster_epoch = excluded.cluster_epoch,
              active_nid    = excluded.active_nid,
              source        = excluded.source,
              updated_at    = excluded.updated_at
            """;
        up.Parameters.AddWithValue("$ca",  clusterAnchor);
        up.Parameters.AddWithValue("$ce",  (long)clusterEpoch);
        up.Parameters.AddWithValue("$nid", activeNid);
        up.Parameters.AddWithValue("$src", source);
        up.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        up.ExecuteNonQuery();

        return ClusterOwnershipOutcome.Applied;
    }

    private static ulong? ReadStoredEpoch(SqliteConnection conn, string nid)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT cluster_epoch FROM announcements WHERE nid = $nid";
        cmd.Parameters.AddWithValue("$nid", nid);
        var raw = cmd.ExecuteScalar();
        return raw is null or DBNull ? null : (ulong)Convert.ToInt64(raw);
    }

    private static ClusterOwnershipEntry MapOwnership(SqliteDataReader rdr) => new(
        ClusterAnchor: rdr.GetString(0),
        ClusterEpoch:  (ulong)rdr.GetInt64(1),
        ActiveNid:     rdr.GetString(2),
        Source:        rdr.GetString(3),
        UpdatedAt:     rdr.GetString(4));

    public NdpResolveResult? Resolve(string target)
    {
        using var conn = Open();
        Purge(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT nid, addresses_json, ttl FROM announcements";
        using var rdr   = cmd.ExecuteReader();
        while (rdr.Read())
        {
            var nid      = rdr.GetString(0);
            var addrJson = rdr.GetString(1);
            var ttl      = (uint)rdr.GetInt64(2);

            if (!InMemoryNdpRegistry.NwpTargetMatchesNid(nid, target)) continue;

            var addrs = JsonSerializer.Deserialize<List<NdpAddress>>(addrJson, _json);
            var addr  = addrs?.FirstOrDefault();
            if (addr is null) continue;

            return new NdpResolveResult { Host = addr.Host, Port = addr.Port, Ttl = ttl };
        }
        return null;
    }

    public IReadOnlyList<AnnounceFrame> GetAll()
    {
        using var conn = Open();
        Purge(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT nid, addresses_json, caps_json, node_type, ttl, timestamp, signature,
                   cluster_anchor, cluster_epoch
            FROM   announcements
            """;
        using var rdr   = cmd.ExecuteReader();
        var list = new List<AnnounceFrame>();
        while (rdr.Read()) list.Add(MapRow(rdr));
        return list;
    }

    public AnnounceFrame? GetByNid(string nid)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT nid, addresses_json, caps_json, node_type, ttl, timestamp, signature,
                   cluster_anchor, cluster_epoch
            FROM   announcements
            WHERE  nid = $nid AND expires_at > $now
            """;
        cmd.Parameters.AddWithValue("$nid", nid);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        using var rdr = cmd.ExecuteReader();
        return rdr.Read() ? MapRow(rdr) : null;
    }

    public NdpResolveResult? ResolveViaDns(string target, IDnsTxtLookup? dnsLookup = null)
    {
        // 1. Try the persistent registry first.
        var cached = Resolve(target);
        if (cached is not null)
            return cached;

        // 2. Extract host from nwp:// URL.
        if (!target.StartsWith("nwp://", StringComparison.OrdinalIgnoreCase))
            return null;
        var rest      = target["nwp://".Length..];
        var slashIdx  = rest.IndexOf('/');
        var host      = slashIdx < 0 ? rest : rest[..slashIdx];
        if (string.IsNullOrWhiteSpace(host))
            return null;

        // 3. DNS TXT fallback on _nps-node.{host}.
        var lookup     = dnsLookup ?? new SystemDnsTxtLookup();
        var txtRecords = lookup.Lookup($"_nps-node.{host}");
        foreach (var txt in txtRecords)
        {
            var result = InMemoryNdpRegistry.ParseNpsTxtRecord(txt, host);
            if (result is not null)
                return result;
        }

        return null;
    }

    /// <summary>Returns the current monotonic graph sequence counter.</summary>
    public ulong GetSeq()
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT seq FROM graph_meta WHERE id = 1";
        return (ulong)(long)(cmd.ExecuteScalar() ?? 0L);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void EnsureSchema()
    {
        using var conn = Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS announcements (
                  nid            TEXT PRIMARY KEY,
                  addresses_json TEXT NOT NULL,
                  caps_json      TEXT NOT NULL,
                  node_type      TEXT,
                  ttl            INTEGER NOT NULL,
                  timestamp      TEXT NOT NULL,
                  signature      TEXT NOT NULL,
                  expires_at     TEXT NOT NULL,
                  cluster_anchor TEXT,
                  cluster_epoch  INTEGER NOT NULL DEFAULT 1
                );
                CREATE INDEX IF NOT EXISTS idx_expires_at ON announcements(expires_at);
                CREATE TABLE IF NOT EXISTS graph_meta (
                  id  INTEGER PRIMARY KEY CHECK (id = 1),
                  seq INTEGER NOT NULL DEFAULT 0
                );
                INSERT OR IGNORE INTO graph_meta (id, seq) VALUES (1, 0);
                CREATE TABLE IF NOT EXISTS cluster_ownership (
                  cluster_anchor TEXT PRIMARY KEY,
                  cluster_epoch  INTEGER NOT NULL,
                  active_nid     TEXT NOT NULL,
                  source         TEXT NOT NULL,
                  updated_at     TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }

        // In-place migration for stores created before NPS-CR-0009 (alpha.16 and earlier):
        // the two cluster columns are added to the existing `announcements` table. This daemon
        // versions its schema with idempotent CREATE/ALTER statements here rather than a
        // db/NNN_*.sql migration directory, so the new columns follow the same convention.
        var columns = new HashSet<string>(StringComparer.Ordinal);
        using (var info = conn.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(announcements)";
            using var rdr = info.ExecuteReader();
            while (rdr.Read()) columns.Add(rdr.GetString(1));
        }

        if (!columns.Contains("cluster_anchor"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE announcements ADD COLUMN cluster_anchor TEXT";
            alter.ExecuteNonQuery();
        }

        if (!columns.Contains("cluster_epoch"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE announcements ADD COLUMN cluster_epoch INTEGER NOT NULL DEFAULT 1";
            alter.ExecuteNonQuery();
        }

        // Indexed after the migration — on a pre-CR-0009 store the column exists only by now.
        using var idx = conn.CreateCommand();
        idx.CommandText = "CREATE INDEX IF NOT EXISTS idx_cluster_anchor ON announcements(cluster_anchor)";
        idx.ExecuteNonQuery();
    }

    private static void Purge(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM announcements WHERE expires_at <= $now";
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static void BumpSeq(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE graph_meta SET seq = seq + 1 WHERE id = 1";
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connStr);
        conn.Open();
        return conn;
    }

    private static AnnounceFrame MapRow(SqliteDataReader rdr)
    {
        var addrs = JsonSerializer.Deserialize<List<NdpAddress>>(rdr.GetString(1), _json)
                    ?? new List<NdpAddress>();
        var caps  = JsonSerializer.Deserialize<List<string>>(rdr.GetString(2), _json)
                    ?? new List<string>();
        return new AnnounceFrame
        {
            Nid           = rdr.GetString(0),
            Addresses     = addrs,
            Capabilities  = caps,
            NodeType      = rdr.IsDBNull(3) ? null : rdr.GetString(3),
            Ttl           = (uint)rdr.GetInt64(4),
            Timestamp     = rdr.GetString(5),
            Signature     = rdr.GetString(6),
            ClusterAnchor = rdr.IsDBNull(7) ? null : rdr.GetString(7),
            ClusterEpoch  = (ulong)rdr.GetInt64(8),
        };
    }
}

/// <summary>
/// A propagated NPS-CR-0009 cluster-ownership tuple: which Anchor owns
/// <paramref name="ClusterAnchor"/>, under which fencing epoch, and where that was learnt.
/// </summary>
/// <param name="ClusterAnchor">The cluster's stable <c>cluster_anchor</c> NID.</param>
/// <param name="ClusterEpoch">The ownership fence (uint64, monotonic per cluster).</param>
/// <param name="ActiveNid">NID of the Anchor that holds ownership at this epoch.</param>
/// <param name="Source">
/// <see cref="SqliteNdpRegistry.LocalSource"/> for tuples derived from local announcements,
/// otherwise the federated peer registry NID the tuple arrived from (NDP §7.6 rule 4).
/// </param>
/// <param name="UpdatedAt">ISO-8601 UTC time the tuple was last advanced.</param>
public sealed record ClusterOwnershipEntry(
    string ClusterAnchor,
    ulong  ClusterEpoch,
    string ActiveNid,
    string Source,
    string UpdatedAt);

/// <summary>Outcome of applying a federated cluster-ownership tuple (NDP §9).</summary>
public enum ClusterOwnershipOutcome
{
    /// <summary>The tuple carried a strictly higher epoch (or the cluster was unknown) and was stored.</summary>
    Applied,

    /// <summary>The tuple was stale (lower epoch) or an idempotent repeat — state unchanged.</summary>
    Ignored,

    /// <summary>Equal epoch naming a different active Anchor — <c>NDP-CLUSTER-SPLIT</c>.</summary>
    Split,
}
