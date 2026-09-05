// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Xunit;

namespace NPS.Daemon.Runner.Tests;

public sealed class RunnerCapabilityContractTests
{
    private static readonly string[] ExpectedCaseIds =
    [
        "TC-N3-Claim-01",
        "TC-N3-Claim-02",
        "TC-N3-Claim-03",
        "TC-N3-Spawn-01",
        "TC-N3-Spawn-02",
        "TC-N3-Spawn-03",
        "TC-N3-Life-01",
        "TC-N3-Life-02",
        "TC-N3-DAG-01",
        "TC-N3-Saga-01",
    ];

    [Fact]
    public void Machine_readable_contract_preserves_implemented_and_unclaimed_boundaries()
    {
        using var document = LoadContract();
        var capabilities = document.RootElement.GetProperty("capabilities");

        Assert.Equal(
            "implemented",
            capabilities.GetProperty("portable_oci_spawn_spec").GetProperty("status").GetString());
        Assert.Equal(
            "shared persistent SQLite state file",
            capabilities.GetProperty("periodic_lease_renewal").GetProperty("scope").GetString());

        var lease = capabilities.GetProperty("task_lease_and_terminal_dedup");
        Assert.Equal("implemented", lease.GetProperty("status").GetString());
        Assert.Equal(
            "runner processes sharing one persistent SQLite state file",
            lease.GetProperty("scope").GetString());
        Assert.Contains(
            lease.GetProperty("not_claimed").EnumerateArray().Select(item => item.GetString()),
            item => item == "multi-node TaskFrame resume");

        var certification = capabilities.GetProperty("node_l3_certification");
        Assert.Equal("not_claimed", certification.GetProperty("status").GetString());
        Assert.Equal(
            "NPS-NODE-L3-MANIFEST.json",
            certification.GetProperty("manifest").GetString());

        using var manifest = LoadJson(
            "conformance/NPS-NODE-L3-MANIFEST.json");
        Assert.Equal(
            "not_claimed",
            manifest.RootElement.GetProperty("certification_claim").GetString());
        Assert.NotEmpty(
            manifest.RootElement.GetProperty("run").GetProperty("commands").EnumerateArray());
        var summary = manifest.RootElement.GetProperty("summary");
        Assert.Equal(3, summary.GetProperty("verified").GetInt32());
        Assert.Equal(5, summary.GetProperty("partial").GetInt32());
        Assert.Equal(2, summary.GetProperty("not_executed").GetInt32());
        var cases = manifest.RootElement.GetProperty("cases");
        Assert.Equal(10, cases.EnumerateObject().Count());
        Assert.Equal(10, summary.GetProperty("total_cases").GetInt32());
        Assert.Equal(ExpectedCaseIds.Order(), cases.EnumerateObject().Select(item => item.Name).Order());
        Assert.Equal(
            summary.GetProperty("verified").GetInt32(),
            cases.EnumerateObject().Count(item =>
                item.Value.GetProperty("status").GetString() == "verified"));
        Assert.Equal(
            summary.GetProperty("partial").GetInt32(),
            cases.EnumerateObject().Count(item =>
                item.Value.GetProperty("status").GetString() == "partial"));
        Assert.Equal(
            summary.GetProperty("not_executed").GetInt32(),
            cases.EnumerateObject().Count(item =>
                item.Value.GetProperty("status").GetString() == "not_executed"));
    }

    private static JsonDocument LoadContract()
    {
        return LoadJson(
            "conformance/NPS-RUNNER-CAPABILITIES.json");
    }

    private static JsonDocument LoadJson(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate))
                return JsonDocument.Parse(File.ReadAllText(candidate));
            current = current.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate repository file '{relativePath}'.");
    }
}
