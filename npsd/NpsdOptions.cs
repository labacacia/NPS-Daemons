// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.Daemon.Npsd;

/// <summary>
/// Configurable knobs for the <c>npsd</c> daemon. All fields have safe
/// defaults; the only thing the operator typically changes is
/// <see cref="DataDir"/> (per-host persistent state) and
/// <see cref="Port"/> / <see cref="Host"/> (bind address).
/// </summary>
public sealed record NpsdOptions
{
    /// <summary>TCP port to bind. Default: 17433 (NPS-1 §2.3 unified port).</summary>
    public int Port { get; init; } = 17433;

    /// <summary>
    /// Bind address. Default: 127.0.0.1 (loopback). Set to 0.0.0.0 only inside
    /// an isolated network namespace — never expose npsd directly to the public
    /// internet (use <c>nps-ingress</c>).
    /// </summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>Maximum time allowed to receive the native NCP preamble.</summary>
    public int NcpPreambleTimeoutMs { get; init; } = 10_000;

    /// <summary>Maximum time allowed to receive the native NCP HelloFrame.</summary>
    public int NcpHelloTimeoutMs { get; init; } = 5_000;

    /// <summary>Maximum native NCP HelloFrame payload accepted before allocation.</summary>
    public int NcpMaxHelloPayloadBytes { get; init; } = ushort.MaxValue;

    /// <summary>Whether native NCP sessions advertise and negotiate Tier-2 MsgPack.</summary>
    public bool NcpEnableMsgPack { get; init; } = true;

    /// <summary>
    /// Persistent state directory. Holds the encrypted root keypair file and
    /// the SQLite databases for sub-NIDs and durable inbox messages.
    /// </summary>
    public string DataDir { get; init; }
        = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "npsd");

    /// <summary>
    /// NID prefix used when minting sub-NIDs on this host. Defaults to
    /// <c>urn:nps:host:&lt;HostFingerprint&gt;</c>; sub-NIDs are minted as
    /// <c>{HostNidPrefixOrComputed}:agent:{identifier}</c>.
    /// </summary>
    /// <remarks>
    /// If null, the runtime computes a deterministic prefix from the
    /// host's root public key fingerprint (first 8 bytes of SHA-256,
    /// hex-encoded). Override only if the host has been registered with
    /// an upstream CA under a different NID and you want sub-NIDs derived
    /// under it.
    /// </remarks>
    public string? HostNidPrefix { get; init; }

    /// <summary>
    /// Default validity window for issued sub-NIDs, in days. Local-host
    /// agent identities are short-lived by design; the upstream CA-issued
    /// host identity is the durable trust anchor.
    /// </summary>
    public int SubNidValidityDays { get; init; } = 7;

    /// <summary>
    /// Number of days before expiry at which a sub-NID may be renewed.
    /// The default one-day window keeps seven-day local credentials short-lived.
    /// </summary>
    public int SubNidRenewalWindowDays { get; init; } = 1;

    /// <summary>Whether npsd emits signed NDP AnnounceFrames for managed agents.</summary>
    public bool NdpAnnounceEnabled { get; init; } = true;

    /// <summary>Base URL of the local NDP Registry.</summary>
    public string NdpRegistryUrl { get; init; } = "http://127.0.0.1:17436";

    /// <summary>Ephemeral AnnounceFrame TTL, capped by NDP at 60 seconds.</summary>
    public int NdpAnnounceTtlSeconds { get; init; } = 60;

    /// <summary>Interval between signed liveness announcements.</summary>
    public int NdpAnnounceIntervalSeconds { get; init; } = 20;

    /// <summary>
    /// Host published in NDP addresses. Defaults to <see cref="Host"/>; operators
    /// binding to 0.0.0.0 should set a routable hostname or address explicitly.
    /// </summary>
    public string? NdpAdvertiseHost { get; init; }

    /// <summary>
    /// Maximum inbox depth per NID. Producers get HTTP 429 when exceeded.
    /// Default 1024; bump only after profiling memory / per-message size.
    /// </summary>
    public int MaxInboxDepthPerNid { get; init; } = 1024;

    /// <summary>
    /// Maximum payload size of a single inbox message, in bytes. Default 64 KiB
    /// matches the NCP default frame size (NPS-1 §3.1 EXT=0).
    /// </summary>
    public int MaxInboxMessageBytes { get; init; } = 64 * 1024;

    /// <summary>
    /// Maximum long-poll wait time, in seconds. Default 30. Clients that
    /// request a longer wait are clamped to this value.
    /// </summary>
    public int MaxInboxWaitSeconds { get; init; } = 30;

    /// <summary>
    /// Read configuration from <c>NPSD_*</c> environment variables. Returns
    /// a populated options instance; missing variables fall back to the
    /// defaults declared above.
    /// </summary>
    public static NpsdOptions FromEnvironment()
    {
        return new NpsdOptions
        {
            Port = int.TryParse(Environment.GetEnvironmentVariable("NPSD_PORT"), out var p) ? p : 17433,
            Host = Environment.GetEnvironmentVariable("NPSD_HOST") ?? "127.0.0.1",
            NcpPreambleTimeoutMs = ParsePositiveInt("NPSD_NCP_PREAMBLE_TIMEOUT_MS", 10_000),
            NcpHelloTimeoutMs = ParsePositiveInt("NPSD_NCP_HELLO_TIMEOUT_MS", 5_000),
            NcpMaxHelloPayloadBytes = ParsePositiveInt("NPSD_NCP_MAX_HELLO_PAYLOAD_BYTES", ushort.MaxValue),
            NcpEnableMsgPack = ParseBool("NPSD_NCP_ENABLE_MSGPACK", true),
            DataDir = Environment.GetEnvironmentVariable("NPSD_DATA_DIR")
                      ?? Path.Combine(
                          Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                          "npsd"),
            HostNidPrefix = Environment.GetEnvironmentVariable("NPSD_HOST_NID_PREFIX"),
            SubNidValidityDays = int.TryParse(Environment.GetEnvironmentVariable("NPSD_SUB_NID_VALIDITY_DAYS"), out var d) ? d : 7,
            SubNidRenewalWindowDays = ParsePositiveInt("NPSD_SUB_NID_RENEWAL_WINDOW_DAYS", 1),
            NdpAnnounceEnabled = ParseBool("NPSD_NDP_ANNOUNCE_ENABLED", true),
            NdpRegistryUrl = Environment.GetEnvironmentVariable("NPSD_NDP_REGISTRY_URL")
                             ?? "http://127.0.0.1:17436",
            NdpAnnounceTtlSeconds = Math.Clamp(
                ParsePositiveInt("NPSD_NDP_ANNOUNCE_TTL_SECONDS", 60), 1, 60),
            NdpAnnounceIntervalSeconds = ParsePositiveInt("NPSD_NDP_ANNOUNCE_INTERVAL_SECONDS", 20),
            NdpAdvertiseHost = Environment.GetEnvironmentVariable("NPSD_NDP_ADVERTISE_HOST"),
            MaxInboxDepthPerNid = int.TryParse(Environment.GetEnvironmentVariable("NPSD_MAX_INBOX_DEPTH_PER_NID"), out var m) ? m : 1024,
            MaxInboxMessageBytes = int.TryParse(Environment.GetEnvironmentVariable("NPSD_MAX_INBOX_MESSAGE_BYTES"), out var b) ? b : 64 * 1024,
            MaxInboxWaitSeconds = int.TryParse(Environment.GetEnvironmentVariable("NPSD_MAX_INBOX_WAIT_SECONDS"), out var w) ? w : 30,
        };
    }

    private static int ParsePositiveInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0
            ? value
            : fallback;

    private static bool ParseBool(string name, bool fallback) =>
        bool.TryParse(Environment.GetEnvironmentVariable(name), out var value)
            ? value
            : fallback;
}
