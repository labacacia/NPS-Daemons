// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text.Json;
using Xunit;

namespace NPS.Daemon.Ingress.Tests;

public sealed class IngressTlsEvidenceManifestTests
{
    private static readonly string[] ExpectedIds =
    [
        "TC-N2-Tls-01",
        "TC-N2-Tls-02",
        "TC-N2-Tls-03",
        "TC-N2-Tls-04",
    ];

    [Fact]
    public void Evidence_manifest_covers_the_exact_executable_tls_family()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "conformance",
            "NPS-NODE-L2-TLS-EVIDENCE.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        Assert.Equal("NPS-Node-L2", root.GetProperty("profile").GetString());
        Assert.Equal("0.7", root.GetProperty("profile_version").GetString());
        Assert.Equal(
            "NPS-NODE-L2-FAMILY-IMPLEMENTATION-MANIFEST",
            root.GetProperty("artifact").GetString());
        Assert.Equal("family_only", root.GetProperty("certification_claim").GetString());
        Assert.Equal("executable", root.GetProperty("evidence_status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(
            root.GetProperty("run").GetProperty("command").GetString()));
        var cases = root.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(ExpectedIds, cases.Select(c => c.GetProperty("id").GetString()));
        Assert.All(cases, c => Assert.Equal("pass", c.GetProperty("result").GetString()));
        Assert.Equal(4, root.GetProperty("summary").GetProperty("pass").GetInt32());
        Assert.Equal(0, root.GetProperty("summary").GetProperty("fail").GetInt32());
        Assert.Equal(4, root.GetProperty("summary").EnumerateObject().Sum(item => item.Value.GetInt32()));

        var testTraits = typeof(IngressTlsConformanceTests)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .SelectMany(method => method.GetCustomAttributesData()
                .Where(attribute => attribute.AttributeType == typeof(TraitAttribute))
                .Where(attribute =>
                    attribute.ConstructorArguments.Count == 2
                    && Equals(attribute.ConstructorArguments[0].Value, "ConformanceId"))
                .Select(attribute => (string)attribute.ConstructorArguments[1].Value!))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(ExpectedIds, testTraits);

        var evidenceMethods = cases
            .Select(c => c.GetProperty("evidence").GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(
            typeof(IngressTlsConformanceTests).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(method => method.Name.StartsWith("TC_N2_Tls_", StringComparison.Ordinal)),
            method => Assert.Contains(method.Name, evidenceMethods));
    }
}
