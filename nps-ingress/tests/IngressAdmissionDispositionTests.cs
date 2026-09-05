// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Xunit;

namespace NPS.Daemon.Ingress.Tests;

public sealed class IngressAdmissionDispositionTests
{
    private static readonly string[] HistoricalIds =
    [
        "rate-limiting",
        "neuronhub-customer-authentication",
        "cgn-debit",
        "reputation-policy",
        "anchor-middleware",
        "ddos-policy",
    ];

    [Fact]
    public void Disposition_claims_only_the_executable_transport_contract()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "conformance",
            "NPS-INGRESS-ADMISSION-DISPOSITION.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        Assert.Equal(
            "NCP-over-TLS transport terminator",
            root.GetProperty("iut").GetProperty("role").GetString());
        Assert.Equal(
            "NPS-RFC-0006 §6",
            root.GetProperty("current_contract").GetProperty("specification").GetString());
        Assert.Equal(
            "implemented",
            root.GetProperty("current_contract").GetProperty("status").GetString());
        Assert.Equal(
            ["TC-N2-Tls-01", "TC-N2-Tls-02", "TC-N2-Tls-03", "TC-N2-Tls-04"],
            root.GetProperty("current_contract").GetProperty("evidence")
                .EnumerateArray().Select(item => item.GetString()));

        var historical = root.GetProperty("historical_items").EnumerateArray().ToArray();
        Assert.Equal(HistoricalIds, historical.Select(item => item.GetProperty("id").GetString()));
        Assert.All(
            historical,
            item => Assert.NotEqual("implemented", item.GetProperty("status").GetString()));

        var testMethods = typeof(IngressTlsConformanceTests)
            .GetMethods()
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);
        var hardeningEvidence = root.GetProperty("transport_hardening")
            .EnumerateArray()
            .SelectMany(item => item.GetProperty("evidence").GetString()!
                .Split("; ", StringSplitOptions.RemoveEmptyEntries));
        Assert.All(hardeningEvidence, method => Assert.Contains(method, testMethods));

        var health = IngressHealthSnapshot.Create(new IngressOptions());
        Assert.Equal("NPS-RFC-0006 §6 transport admission only", health.AdmissionContract);
        Assert.Equal(6, health.Unsupported.Count);
        Assert.Contains(
            "claims only the RFC-0006 transport admission boundary",
            root.GetProperty("claim").GetString(),
            StringComparison.Ordinal);
    }
}
