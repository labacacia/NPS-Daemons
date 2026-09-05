// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using NSec.Cryptography;

namespace NPS.Daemon.Npsd;

/// <summary>
/// The host's root Ed25519 keypair, persisted to disk and loaded on startup.
/// </summary>
/// <remarks>
/// alpha.3: plain PKCS#8 with POSIX file mode <c>0600</c> — satisfies
/// NPS-Node Profile L1 conformance test
/// <c>TC-N1-NIP-01 — Root keypair generation and permission</c>. We do
/// not yet use NPS.NIP's AES-256-GCM encrypted-at-rest format because the
/// passphrase management story for an unattended host-local daemon is not
/// settled; that decision lands with NPS-RFC-0002 (X.509 + ACME) in alpha.4.
///
/// The in-memory <see cref="Key"/> handle is shared with anything that needs
/// to sign on behalf of the host (sub-NID issuance and host-bound protection
/// of managed agent keys used for publisher-signed AnnounceFrames).
/// </remarks>
public sealed class RootIdentity : IDisposable
{
    private readonly Key _privateKey;

    /// <summary>NSec key handle. Use with <c>NPS.NIP.Crypto.NipSigner.Sign</c>.</summary>
    public Key Key => _privateKey;

    /// <summary>Public key half of <see cref="Key"/>.</summary>
    public PublicKey PublicKey => _privateKey.PublicKey;

    /// <summary>
    /// Deterministic short fingerprint of the public key. First 8 bytes
    /// of <c>SHA-256(PKIX-encoded-public-key)</c>, lowercase hex (16 chars).
    /// Stable across restarts; used to derive default NID prefixes and as
    /// an operator-friendly host identifier in logs.
    /// </summary>
    public string HostFingerprint { get; }

    /// <summary>
    /// Encoded form of <see cref="PublicKey"/> in <c>ed25519:{base64url}</c>
    /// for emitting in IdentFrame <c>pub_key</c> fields.
    /// </summary>
    public string PublicKeyEncoded { get; }

    private RootIdentity(Key privateKey, string fingerprint, string publicKeyEncoded)
    {
        _privateKey      = privateKey;
        HostFingerprint  = fingerprint;
        PublicKeyEncoded = publicKeyEncoded;
    }

    /// <summary>
    /// Loads the root keypair from <paramref name="dataDir"/>, generating
    /// it on first run. The key file lives at
    /// <c>{dataDir}/root.ed25519.pkcs8</c> and is written with POSIX mode
    /// <c>0600</c> on Linux/macOS.
    /// </summary>
    public static RootIdentity LoadOrCreate(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var keyPath = Path.Combine(dataDir, "root.ed25519.pkcs8");

        byte[] priv;
        if (File.Exists(keyPath))
        {
            priv = File.ReadAllBytes(keyPath);
        }
        else
        {
            using var ed = Key.Create(
                SignatureAlgorithm.Ed25519,
                new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
            priv = ed.Export(KeyBlobFormat.PkixPrivateKey);
            File.WriteAllBytes(keyPath, priv);
            // L1 conformance — TC-N1-NIP-01: file MUST be 0600 on POSIX.
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    keyPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        var imported = Key.Import(
            SignatureAlgorithm.Ed25519,
            priv,
            KeyBlobFormat.PkixPrivateKey,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        var pubPkix       = imported.PublicKey.Export(KeyBlobFormat.PkixPublicKey);
        var fingerprint   = Convert.ToHexString(SHA256.HashData(pubPkix).AsSpan(0, 8)).ToLowerInvariant();
        var pubRaw        = imported.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        var pubBase64Url  = "ed25519:" + Convert.ToBase64String(pubRaw)
                              .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return new RootIdentity(imported, fingerprint, pubBase64Url);
    }

    /// <inheritdoc/>
    public void Dispose() => _privateKey.Dispose();
}
