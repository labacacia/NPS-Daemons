// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.Daemon.Runner;
using Xunit;

namespace NPS.Daemon.Runner.Tests;

/// <summary>Tests for the NPS-CR-0007 §4 task-claim lease protocol.</summary>
public sealed class LeaseStoreTests
{
    private const string Task = "task-1";
    private const string Dedup = "dedup-abc";

    [Fact]
    public void Concurrent_claim_grants_one_and_conflicts_the_other()
    {
        var store = new LeaseStore();
        var first = store.TryClaim(Task, "runner-A", 60, Dedup);
        var second = store.TryClaim(Task, "runner-B", 60, Dedup);

        Assert.Equal(ClaimResult.Granted, first.Result);
        Assert.Equal(ClaimResult.Conflict, second.Result);
        Assert.Equal("NOP-CLAIM-CONFLICT", second.ErrorCode);
    }

    [Fact]
    public void Owning_runner_reclaim_renews_not_conflicts()
    {
        var store = new LeaseStore();
        store.TryClaim(Task, "runner-A", 60, Dedup);
        var again = store.TryClaim(Task, "runner-A", 60, Dedup);
        Assert.Equal(ClaimResult.Granted, again.Result);
    }

    [Fact]
    public void Expired_lease_cannot_be_renewed_by_the_former_owner()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new LeaseStore(() => now);
        store.TryClaim(Task, "runner-A", 10, Dedup);

        now = now.AddSeconds(10);

        Assert.False(store.Renew(Task, "runner-A", 10));
        Assert.Equal(
            ClaimResult.Reclaimed,
            store.TryClaim(Task, "runner-B", 10, Dedup).Result);
    }

    [Fact]
    public void Expired_lease_is_reclaimable_by_another_runner()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new LeaseStore(() => now);
        store.TryClaim(Task, "runner-A", 10, Dedup);   // min lease = 10s

        now = now.AddSeconds(11);                       // lease expired
        var reclaim = store.TryClaim(Task, "runner-B", 60, Dedup);
        Assert.Equal(ClaimResult.Reclaimed, reclaim.Result);
    }

    [Fact]
    public void Lease_seconds_are_clamped_to_bounds()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new LeaseStore(() => now);
        store.TryClaim(Task, "runner-A", 5, Dedup);     // below min(10) → clamped to 10

        now = now.AddSeconds(9);                         // still within the clamped 10s lease
        Assert.Equal(ClaimResult.Conflict, store.TryClaim(Task, "runner-B", 60, Dedup).Result);
    }

    [Fact]
    public void Terminal_node_dedup_prevents_reexecution_on_reclaim()
    {
        var store = new LeaseStore();
        Assert.False(store.IsNodeDone(Dedup, "node-1"));
        store.MarkNodeDone(Dedup, "node-1");
        Assert.True(store.IsNodeDone(Dedup, "node-1"));
        // A different dedup key (different task/dag) is a distinct unit of work.
        Assert.False(store.IsNodeDone("other-dedup", "node-1"));
    }

    [Fact]
    public void Release_frees_the_lease_for_another_runner()
    {
        var store = new LeaseStore();
        store.TryClaim(Task, "runner-A", 60, Dedup);
        store.Release(Task, "runner-A");
        Assert.Equal(ClaimResult.Granted, store.TryClaim(Task, "runner-B", 60, Dedup).Result);
    }

    [Fact]
    public void Release_by_non_owner_is_ignored()
    {
        var store = new LeaseStore();
        store.TryClaim(Task, "runner-A", 60, Dedup);
        store.Release(Task, "runner-B");                 // not the owner — no-op
        Assert.Equal(ClaimResult.Conflict, store.TryClaim(Task, "runner-B", 60, Dedup).Result);
    }

    [Fact]
    public void DedupKey_is_deterministic_and_distinct_per_input()
    {
        var k1 = LeaseStore.ComputeDedupKey("task-1", "dagA");
        var k2 = LeaseStore.ComputeDedupKey("task-1", "dagA");
        var k3 = LeaseStore.ComputeDedupKey("task-1", "dagB");
        Assert.Equal(k1, k2);
        Assert.NotEqual(k1, k3);
        Assert.Equal(64, k1.Length); // sha256 hex
    }
}
