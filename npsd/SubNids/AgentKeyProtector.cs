// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using NPS.NIP.Crypto;
using NSec.Cryptography;

namespace NPS.Daemon.Npsd.SubNids;

/// <summary>
/// Protects npsd-managed agent keys at rest with a key derived from the
/// host root identity. Moving only the SQLite file to another host is not
/// sufficient to recover or use the agent keys.
/// </summary>
public sealed class AgentKeyProtector : IDisposable
{
    private const string Prefix = "aes256gcm:v1:";
    private readonly byte[] _key;

    public AgentKeyProtector(RootIdentity root)
    {
        var rootPrivate = root.Key.Export(KeyBlobFormat.RawPrivateKey);
        _key = HMACSHA256.HashData(
            rootPrivate,
            Encoding.UTF8.GetBytes("npsd/managed-agent-key/v1"));
        CryptographicOperations.ZeroMemory(rootPrivate);
    }

    public string Protect(string nid, ReadOnlySpan<byte> privateKey)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[privateKey.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, privateKey, ciphertext, tag, Encoding.UTF8.GetBytes(nid));

        var envelope = new byte[nonce.Length + tag.Length + ciphertext.Length];
        nonce.CopyTo(envelope, 0);
        tag.CopyTo(envelope, nonce.Length);
        ciphertext.CopyTo(envelope, nonce.Length + tag.Length);
        return Prefix + NipSigner.Base64Url(envelope);
    }

    public Key Unprotect(string nid, string protectedValue)
    {
        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
            throw new CryptographicException("Unsupported managed agent key envelope.");

        var envelope = DecodeBase64Url(protectedValue[Prefix.Length..]);
        if (envelope.Length < 29)
            throw new CryptographicException("Managed agent key envelope is truncated.");

        var nonce = envelope.AsSpan(0, 12);
        var tag = envelope.AsSpan(12, 16);
        var ciphertext = envelope.AsSpan(28);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(_key, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(nid));
            return Key.Import(
                SignatureAlgorithm.Ed25519,
                plaintext,
                KeyBlobFormat.RawPrivateKey,
                new KeyCreationParameters
                {
                    ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
                });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => string.Empty,
        };
        return Convert.FromBase64String(padded);
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(_key);
}
