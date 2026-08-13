// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.Daemon.Runner;
using Xunit;

namespace NPS.Daemon.Runner.Tests;

public sealed class PortableRuntimeConformanceTests
{
    [Fact]
    public void Daemon_owned_runtime_vectors_match_nop_0_9_profile()
    {
        using var fixture = LoadFixture();
        var count = 0;
        foreach (var vector in fixture.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var category = vector.GetProperty("category").GetString()!;
            if (category is not ("lease" or "spawn_spec" or "lifecycle" or "dedup_key"))
                continue;

            count++;
            var actual = category switch
            {
                "lease" => EvaluateLease(vector.GetProperty("input")),
                "spawn_spec" => EvaluateSpawnSpec(vector.GetProperty("input")),
                "lifecycle" => EvaluateLifecycle(vector.GetProperty("input")),
                "dedup_key" => EvaluateDedupKey(vector.GetProperty("input")),
                _ => throw new InvalidOperationException(),
            };

            Assert.True(
                JsonElement.DeepEquals(vector.GetProperty("expected"), actual),
                $"{vector.GetProperty("id").GetString()}\nexpected: {vector.GetProperty("expected")}\nactual: {actual}");
        }

        Assert.Equal(9, count);
    }

    [Fact]
    public void Legacy_direct_subprocess_shape_remains_supported()
    {
        const string Json = """
            {
              "task_id": "legacy-task",
              "command": "/usr/bin/printf",
              "args": ["hello"],
              "env": {"MODE": "legacy"}
            }
            """;

        Assert.True(SpawnSpecParser.TryParse(Json, out var spec, out var error));
        Assert.Null(error);
        Assert.NotNull(spec);
        Assert.False(spec.IsPortableOci);
        Assert.Equal("/usr/bin/printf", spec.Command);
        Assert.Equal(["hello"], spec.Args);
    }

    private static JsonElement EvaluateLease(JsonElement input)
    {
        var origin = DateTimeOffset.UnixEpoch;
        var now = origin;
        var store = new LeaseStore(() => now);
        var outcomes = new List<string>();
        foreach (var item in input.GetProperty("events").EnumerateArray())
        {
            now = origin.AddSeconds(item.GetProperty("at").GetInt32());
            var operation = item.GetProperty("op").GetString();
            switch (operation)
            {
                case "claim":
                    var claim = store.TryClaim(
                        item.GetProperty("task_id").GetString()!,
                        item.GetProperty("runner_nid").GetString()!,
                        item.GetProperty("lease_seconds").GetInt32(),
                        item.GetProperty("dedup_key").GetString()!);
                    outcomes.Add(claim.Result switch
                    {
                        ClaimResult.Granted => "granted",
                        ClaimResult.Conflict => "conflict",
                        ClaimResult.Reclaimed => "reclaimed",
                        _ => throw new InvalidOperationException(),
                    });
                    break;
                case "renew":
                    outcomes.Add(store.Renew(
                        item.GetProperty("task_id").GetString()!,
                        item.GetProperty("runner_nid").GetString()!,
                        item.GetProperty("lease_seconds").GetInt32())
                        ? "granted"
                        : "conflict");
                    break;
                case "mark_terminal":
                    store.MarkNodeDone(
                        item.GetProperty("dedup_key").GetString()!,
                        item.GetProperty("node_id").GetString()!);
                    outcomes.Add("recorded");
                    break;
                case "is_terminal":
                    outcomes.Add(store.IsNodeDone(
                        item.GetProperty("dedup_key").GetString()!,
                        item.GetProperty("node_id").GetString()!)
                        ? "terminal"
                        : "pending");
                    break;
                default:
                    throw new InvalidOperationException($"Unknown lease operation '{operation}'.");
            }
        }
        return JsonSerializer.SerializeToElement(new { outcomes });
    }

    private static JsonElement EvaluateSpawnSpec(JsonElement input)
    {
        var valid = SpawnSpecParser.TryParse(
            input.GetProperty("spawn_spec").GetRawText(),
            out _,
            out var errorCode);
        return JsonSerializer.SerializeToElement(new
        {
            error = valid ? null : errorCode,
        });
    }

    private static JsonElement EvaluateLifecycle(JsonElement input)
    {
        var reason = RunnerLifecyclePolicy.BreachReason(
            TimeSpan.FromSeconds(input.GetProperty("elapsed_seconds").GetInt32()),
            TimeSpan.FromSeconds(input.GetProperty("idle_seconds").GetInt32()),
            input.GetProperty("idle_timeout_seconds").GetInt32(),
            input.GetProperty("max_runtime_seconds").GetInt32());
        var done = input.TryGetProperty("worker_terminal", out var terminal) &&
            terminal.ValueKind == JsonValueKind.String &&
            string.Equals(terminal.GetString(), "done", StringComparison.Ordinal);
        return JsonSerializer.SerializeToElement(new
        {
            state = reason is null && done ? "completed" : "failed",
            error = reason is not null
                ? RunnerCodes.MapKilledReason(reason)
                : done
                    ? null
                    : "NOP-DELEGATE-REJECTED",
        });
    }

    private static JsonElement EvaluateDedupKey(JsonElement input)
        => JsonSerializer.SerializeToElement(new
        {
            value = LeaseStore.ComputeDedupKey(
                input.GetProperty("task_id").GetString()!,
                input.GetProperty("dag_hash").GetString()!),
        });

    private static JsonDocument LoadFixture()
    {
        const string RelativePath =
            "spec/conformance/nop/runtime_security_vectors.json";
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, RelativePath);
            if (File.Exists(candidate))
                return JsonDocument.Parse(File.ReadAllText(candidate));
            current = current.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate repository file '{RelativePath}'.");
    }
}
