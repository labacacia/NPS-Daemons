// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.Daemon.Ingress;

internal sealed record IngressOptions
{
    /// <summary>HTTP health/observability port (unchanged from the Phase-1 skeleton).</summary>
    public int HealthPort { get; init; } = 8080;

    /// <summary>Native-mode TLS termination port (NPS-RFC-0006 §6). 0 disables the listener.</summary>
    public int TlsPort { get; init; } = 17443;

    /// <summary>Local backend the terminated NCP byte stream is proxied to (npsd, NPS-1 port).</summary>
    public string BackendHost { get; init; } = "127.0.0.1";
    public int BackendPort { get; init; } = 17433;

    /// <summary>PKCS#12 (.pfx) server certificate path. When null, the TLS listener stays idle.</summary>
    public string? ServerCertPath { get; init; }
    public string? ServerCertPassword { get; init; }

    /// <summary>Directory of trust-anchor CA certificates (PEM/DER) for client-cert validation.</summary>
    public string? TrustAnchorsDir { get; init; }

    /// <summary>Require mutual TLS (client certificate). Default true (NPS-RFC-0006 §6.3).</summary>
    public bool RequireClientCert { get; init; } = true;

    /// <summary>
    /// Upper bound (bytes) on a single native-mode handshake frame the L2 terminator will buffer
    /// while peeking for the IdentFrame. The frame length prefix is attacker-controlled (up to the
    /// 4 GB EXT-mode max), so an unbounded read is an OOM DoS; handshake frames (Hello/Ident) are
    /// small, so 1 MiB is a generous ceiling. Oversized frames close the connection.
    /// </summary>
    public int MaxHandshakeFrameBytes { get; init; } = 1 << 20; // 1 MiB

    /// <summary>
    /// Maximum time allowed for the preamble, HelloFrame, and authenticated IdentFrame admission
    /// sequence. This bounds unauthenticated slow connections at the public TLS edge.
    /// </summary>
    public int HandshakeTimeoutMs { get; init; } = 10_000;

    public static IngressOptions FromEnvironment() => new()
    {
        HealthPort = IntEnv("NPSINGRESS_PORT", 8080),
        TlsPort = IntEnv("NPSINGRESS_TLS_PORT", 17443),
        BackendHost = Env("NPSINGRESS_BACKEND_HOST", "127.0.0.1"),
        BackendPort = IntEnv("NPSINGRESS_BACKEND_PORT", 17433),
        ServerCertPath = EnvOrNull("NPSINGRESS_CERT_PATH"),
        ServerCertPassword = EnvOrNull("NPSINGRESS_CERT_PASSWORD"),
        TrustAnchorsDir = EnvOrNull("NPSINGRESS_TRUST_ANCHORS_DIR"),
        RequireClientCert = BoolEnv("NPSINGRESS_REQUIRE_CLIENT_CERT", true),
        MaxHandshakeFrameBytes = PositiveIntEnv("NPSINGRESS_MAX_HANDSHAKE_FRAME_BYTES", 1 << 20),
        HandshakeTimeoutMs = PositiveIntEnv("NPSINGRESS_HANDSHAKE_TIMEOUT_MS", 10_000),
    };

    static string Env(string k, string d) => Environment.GetEnvironmentVariable(k) ?? d;
    static string? EnvOrNull(string k) => Environment.GetEnvironmentVariable(k);
    static int IntEnv(string k, int d) => int.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : d;
    static int PositiveIntEnv(string k, int d) =>
        int.TryParse(Environment.GetEnvironmentVariable(k), out var v) && v > 0 ? v : d;
    static bool BoolEnv(string k, bool d) => bool.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : d;
}
