// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.Daemon.Runner;
using Xunit;

namespace NPS.Daemon.Runner.Tests;

public sealed class LeaseRenewalLoopTests
{
    [Fact]
    public async Task Periodic_loop_renews_the_durable_lease_at_half_window()
    {
        var now = DateTimeOffset.UnixEpoch;
        using var leases = LeaseStore.CreateInMemoryForTests(() => now);
        leases.TryClaim("task-1", "runner-A", 10, "dedup");
        using var workerCts = new CancellationTokenSource();
        var delayCalls = 0;
        var loop = new LeaseRenewalLoop(leases, (interval, ct) =>
        {
            Assert.Equal(TimeSpan.FromSeconds(5), interval);
            delayCalls++;
            if (delayCalls == 1)
            {
                now = now.AddSeconds(5);
                return Task.CompletedTask;
            }

            workerCts.Cancel();
            return Task.FromCanceled(ct);
        });

        Assert.True(await loop.RunAsync("task-1", "runner-A", 10, workerCts));

        now = DateTimeOffset.UnixEpoch.AddSeconds(11);
        Assert.Equal(
            ClaimResult.Conflict,
            leases.TryClaim("task-1", "runner-B", 10, "dedup").Result);
    }

    [Fact]
    public async Task Lost_lease_cancels_the_worker_and_reports_ownership_loss()
    {
        var now = DateTimeOffset.UnixEpoch;
        using var leases = LeaseStore.CreateInMemoryForTests(() => now);
        leases.TryClaim("task-1", "runner-A", 10, "dedup");
        using var workerCts = new CancellationTokenSource();
        var loop = new LeaseRenewalLoop(leases, (_, _) =>
        {
            now = now.AddSeconds(10);
            return Task.CompletedTask;
        });

        Assert.False(await loop.RunAsync("task-1", "runner-A", 10, workerCts));
        Assert.True(workerCts.IsCancellationRequested);
    }
}
