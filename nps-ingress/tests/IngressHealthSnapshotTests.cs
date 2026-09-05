// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.Daemon.Ingress;
using Xunit;

namespace NPS.Daemon.Ingress.Tests;

public sealed class IngressHealthSnapshotTests
{
    [Fact]
    public void Reports_native_tls_configuration_and_implemented_boundaries()
    {
        var report = IngressHealthSnapshot.Create(new IngressOptions
        {
            TlsPort = 17443,
            ServerCertPath = "/run/secrets/ingress.pfx",
            RequireClientCert = true,
            TrustAnchorsDir = Path.GetTempPath(),
            BackendHost = "npsd",
            BackendPort = 17433,
        });

        Assert.True(report.NativeTls.Implemented);
        Assert.True(report.NativeTls.ConfigurationPresent);
        Assert.Equal("TLS 1.3", report.NativeTls.MinimumProtocol);
        Assert.Equal("nps/1.0", report.NativeTls.Alpn);
        Assert.True(report.NativeTls.TrustAnchorValidation);
        Assert.True(report.NativeTls.TrustAnchorsConfigured);
        Assert.True(report.NativeTls.InlineIdentFrameNidBinding);
        Assert.True(report.NativeTls.HalfCloseResponseDrain);
        Assert.Equal("NCP-NID-MISMATCH", report.NativeTls.NidMismatchCode);
        Assert.Equal(10_000, report.NativeTls.HandshakeTimeoutMs);
        Assert.Equal("npsd:17433", report.NativeTls.Backend);
    }

    [Fact]
    public void Does_not_claim_tls_is_configured_or_full_l2_certification_by_default()
    {
        var report = IngressHealthSnapshot.Create(new IngressOptions());

        Assert.False(report.NativeTls.ConfigurationPresent);
        Assert.False(report.NativeTls.TrustAnchorsConfigured);
        Assert.Equal(
            "TC-N2-Tls-01..04 executable evidence present; full NPS-Node-L2 not claimed",
            report.Certification);
        Assert.Equal("NPS-RFC-0006 §6 transport admission only", report.AdmissionContract);
        Assert.Equal(
            "conformance/NPS-INGRESS-ADMISSION-DISPOSITION.json",
            report.AdmissionDisposition);
        Assert.Contains("rate limiting", report.Unsupported);
        Assert.Contains("NPS.NWP.Anchor middleware", report.Unsupported);
    }
}
