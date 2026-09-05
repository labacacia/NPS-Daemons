// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.Daemon.Runner;
using Xunit;

namespace NPS.Daemon.Runner.Tests;

/// <summary>Tests for the durable NPS-CR-0007 §4 task-claim protocol.</summary>
public sealed class LeaseStoreTests
{
    private const string TaskId = "task-1";
    private const string Dedup = "dedup-abc";

    [Fact]
    public async Task Concurrent_claim_across_file_backed_runner_instances_grants_exactly_one()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"nps-runner-concurrent-{Guid.NewGuid():N}");
        var statePath = Path.Combine(root, "leases.db");
        try
        {
            using var first = new LeaseStore(statePath, "instance-A");
            using var second = new LeaseStore(statePath, "instance-B");
            using var start = new ManualResetEventSlim(false);

            var firstClaim = Task.Run(() =>
            {
                start.Wait();
                return first.TryClaim(TaskId, "runner", 60, Dedup);
            });
            var secondClaim = Task.Run(() =>
            {
                start.Wait();
                return second.TryClaim(TaskId, "runner", 60, Dedup);
            });
            start.Set();

            var claims = await Task.WhenAll(firstClaim, secondClaim);

            Assert.Single(claims, claim => claim.Result == ClaimResult.Granted);
            var conflict = Assert.Single(claims, claim => claim.Result == ClaimResult.Conflict);
            Assert.Equal(LeaseStore.ClaimConflictCode, conflict.ErrorCode);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Same_owner_claim_renews_without_conflict()
    {
        using var store = LeaseStore.CreateInMemoryForTests(ownerInstanceId: "instance-A");
        store.TryClaim(TaskId, "runner-A", 60, Dedup);

        Assert.Equal(
            ClaimResult.Granted,
            store.TryClaim(TaskId, "runner-A", 60, Dedup).Result);
    }

    [Fact]
    public void Expired_lease_is_reclaimable_but_stale_owner_cannot_renew_or_complete()
    {
        var now = DateTimeOffset.UnixEpoch;
        var database = $"lease-reclaim-{Guid.NewGuid():N}";
        using var stale = LeaseStore.CreateSharedInMemoryForTests(
            database,
            "instance-A",
            () => now);
        using var replacement = LeaseStore.CreateSharedInMemoryForTests(
            database,
            "instance-B",
            () => now);
        stale.TryClaim(TaskId, "runner", 10, Dedup);

        now = now.AddSeconds(10);

        Assert.Equal(
            ClaimResult.Reclaimed,
            replacement.TryClaim(TaskId, "runner", 10, Dedup).Result);
        Assert.False(stale.Renew(TaskId, "runner", 10));
        Assert.False(stale.TryCompleteTask(TaskId, "runner", Dedup));
        Assert.False(stale.IsNodeDone(Dedup, TaskId));
    }

    [Fact]
    public void Lease_seconds_are_clamped_to_minimum()
    {
        var now = DateTimeOffset.UnixEpoch;
        var database = $"lease-clamp-{Guid.NewGuid():N}";
        using var first = LeaseStore.CreateSharedInMemoryForTests(
            database,
            "instance-A",
            () => now);
        using var second = LeaseStore.CreateSharedInMemoryForTests(
            database,
            "instance-B",
            () => now);
        first.TryClaim(TaskId, "runner", 5, Dedup);

        now = now.AddSeconds(9);

        Assert.Equal(
            ClaimResult.Conflict,
            second.TryClaim(TaskId, "runner", 60, Dedup).Result);
    }

    [Fact]
    public void Terminal_node_dedup_survives_store_reopen_and_reclaim()
    {
        var statePath = Path.Combine(
            Path.GetTempPath(),
            $"nps-runner-lease-{Guid.NewGuid():N}.db");
        var now = DateTimeOffset.UnixEpoch;
        try
        {
            using (var first = new LeaseStore(statePath, "instance-A", () => now))
            {
                first.TryClaim(TaskId, "runner", 10, Dedup);
                Assert.True(first.TryRecordTerminal(TaskId, "runner", Dedup, "node-1"));
            }

            now = now.AddSeconds(11);
            using var restarted = new LeaseStore(statePath, "instance-B", () => now);
            Assert.Equal(
                ClaimResult.Reclaimed,
                restarted.TryClaim(TaskId, "runner", 10, Dedup).Result);
            Assert.True(restarted.IsNodeDone(Dedup, "node-1"));
        }
        finally
        {
            File.Delete(statePath);
        }
    }

    [Fact]
    public void Atomic_completion_persists_terminal_and_suppresses_redelivery()
    {
        var database = $"lease-complete-{Guid.NewGuid():N}";
        using var owner = LeaseStore.CreateSharedInMemoryForTests(database, "instance-A");
        using var redelivery = LeaseStore.CreateSharedInMemoryForTests(database, "instance-B");
        owner.TryClaim(TaskId, "runner", 60, Dedup);

        Assert.True(owner.TryCompleteTask(TaskId, "runner", Dedup));
        Assert.True(owner.IsNodeDone(Dedup, TaskId));
        Assert.Equal(
            ClaimResult.AlreadyCompleted,
            redelivery.TryClaim(TaskId, "runner", 60, Dedup).Result);
    }

    [Fact]
    public void Release_by_non_owner_is_ignored()
    {
        var database = $"lease-release-{Guid.NewGuid():N}";
        using var owner = LeaseStore.CreateSharedInMemoryForTests(database, "instance-A");
        using var other = LeaseStore.CreateSharedInMemoryForTests(database, "instance-B");
        owner.TryClaim(TaskId, "runner", 60, Dedup);

        Assert.False(other.Release(TaskId, "runner"));
        Assert.Equal(
            ClaimResult.Conflict,
            other.TryClaim(TaskId, "runner", 60, Dedup).Result);
    }

    [Fact]
    public void Dedup_key_is_deterministic_and_distinct_per_input()
    {
        var first = LeaseStore.ComputeDedupKey("task-1", "dagA");
        var duplicate = LeaseStore.ComputeDedupKey("task-1", "dagA");
        var different = LeaseStore.ComputeDedupKey("task-1", "dagB");
        Assert.Equal(first, duplicate);
        Assert.NotEqual(first, different);
        Assert.Equal(64, first.Length);
    }
}
