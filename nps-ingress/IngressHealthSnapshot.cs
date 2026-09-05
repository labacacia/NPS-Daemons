// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.Daemon.Ingress;

/// <summary>
/// Machine-readable capability truth for the observability endpoint. Keep unsupported admission
/// controls explicit so deployment tooling cannot mistake the native TLS boundary for complete L2
/// certification.
/// </summary>
internal sealed record IngressHealthSnapshot(
    string Status,
    string Daemon,
    string Version,
    int Layer,
    string Role,
    string SpecReference,
    NativeTlsCapability NativeTls,
    string Certification,
    string AdmissionContract,
    string AdmissionDisposition,
    IReadOnlyList<string> Unsupported)
{
    public static IngressHealthSnapshot Create(IngressOptions options) => new(
        Status: "ok",
        Daemon: "nps-ingress",
        Version: "1.0.0-alpha.18",
        Layer: 2,
        Role: "Internet ingress (L2: NCP-over-TLS terminator)",
        SpecReference: "NPS-RFC-0006 §6; docs/daemons/architecture.md",
        NativeTls: new NativeTlsCapability(
            Implemented: true,
            ConfigurationPresent: options.TlsPort > 0
                && !string.IsNullOrWhiteSpace(options.ServerCertPath),
            Port: options.TlsPort,
            MinimumProtocol: "TLS 1.3",
            Alpn: NcpL2.Alpn,
            MutualTlsSupported: true,
            MutualTlsRequired: options.RequireClientCert,
            TrustAnchorValidation: options.RequireClientCert,
            TrustAnchorsConfigured: !string.IsNullOrWhiteSpace(options.TrustAnchorsDir)
                && Directory.Exists(options.TrustAnchorsDir),
            InlineIdentFrameNidBinding: options.RequireClientCert,
            NidMismatchCode: NcpL2.NidMismatchCode,
            HandshakeTimeoutMs: options.HandshakeTimeoutMs,
            BackendProxy: true,
            HalfCloseResponseDrain: true,
            Backend: $"{options.BackendHost}:{options.BackendPort}"),
        Certification: "TC-N2-Tls-01..04 executable evidence present; full NPS-Node-L2 not claimed",
        AdmissionContract: "NPS-RFC-0006 §6 transport admission only",
        AdmissionDisposition: "conformance/NPS-INGRESS-ADMISSION-DISPOSITION.json",
        Unsupported:
        [
            "rate limiting",
            "NeuronHub customer authentication",
            "CGN debit trigger",
            "NPS-RFC-0004 reputation policy lookup",
            "NPS.NWP.Anchor middleware",
            "DDoS admission policy",
        ]);
}

internal sealed record NativeTlsCapability(
    bool Implemented,
    bool ConfigurationPresent,
    int Port,
    string MinimumProtocol,
    string Alpn,
    bool MutualTlsSupported,
    bool MutualTlsRequired,
    bool TrustAnchorValidation,
    bool TrustAnchorsConfigured,
    bool InlineIdentFrameNidBinding,
    string NidMismatchCode,
    int HandshakeTimeoutMs,
    bool BackendProxy,
    bool HalfCloseResponseDrain,
    string Backend);
