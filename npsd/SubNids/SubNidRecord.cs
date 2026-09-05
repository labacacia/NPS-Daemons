// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.Daemon.Npsd.SubNids;

/// <summary>
/// One issued sub-NID, persisted in the npsd SQLite store.
/// </summary>
public sealed record SubNidRecord
{
    /// <summary>Full NID, e.g. <c>urn:nps:host:&lt;hostfp&gt;:agent:my-worker</c>.</summary>
    public required string Nid { get; init; }

    /// <summary>Public key in <c>ed25519:{base64url}</c> form (raw 32 bytes).</summary>
    public required string PubKey { get; init; }

    /// <summary>
    /// Encrypted form of the agent's private key when npsd minted the
    /// keypair (caller did not bring their own). Null when the caller
    /// supplied their own pub_key (npsd never sees the private half).
    /// AES-256-GCM ciphertext encoded as <c>{12-byte nonce}{16-byte tag}{ciphertext}</c>
    /// base64-encoded.
    /// </summary>
    public string? PrivKeyEncrypted { get; init; }

    /// <summary>Issuer NID, i.e. the host root NID.</summary>
    public required string IssuedBy { get; init; }

    /// <summary>UTC issue time.</summary>
    public required DateTimeOffset IssuedAt { get; init; }

    /// <summary>UTC expiry time.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Hex-encoded serial number, monotonic per-host.</summary>
    public required string Serial { get; init; }

    /// <summary>Capability list as a comma-separated string (storage form).</summary>
    public required string Capabilities { get; init; }

    /// <summary>Scope JSON object (storage form).</summary>
    public required string ScopeJson { get; init; }

    /// <summary>Optional metadata JSON object.</summary>
    public string? MetadataJson { get; init; }

    /// <summary>True once the operator has revoked this sub-NID.</summary>
    public bool Revoked { get; init; }

    /// <summary>UTC revocation time, when <see cref="Revoked"/>.</summary>
    public DateTimeOffset? RevokedAt { get; init; }

    /// <summary>
    /// Revocation reason, when <see cref="Revoked"/>. Free-form short string;
    /// suggested values match NPS-3 §5.3 RevokeFrame reason values.
    /// </summary>
    public string? RevokeReason { get; init; }

    /// <summary>
    /// Last graph sequence reserved for an emitted AnnounceFrame. Persisted so
    /// liveness announcements remain monotonic across daemon restarts.
    /// </summary>
    public ulong GraphSeq { get; init; }
}
