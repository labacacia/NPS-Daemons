// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.Daemon.Runner;

/// <summary>
/// Periodically renews a worker's durable lease. Returning <see langword="false"/>
/// means ownership was lost and the worker cancellation token was cancelled.
/// </summary>
internal sealed class LeaseRenewalLoop
{
    private readonly LeaseStore _leases;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public LeaseRenewalLoop(LeaseStore leases)
        : this(leases, Task.Delay)
    {
    }

    internal LeaseRenewalLoop(
        LeaseStore leases,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _leases = leases;
        _delay = delay;
    }

    public async Task<bool> RunAsync(
        string taskId,
        string runnerNid,
        int leaseSeconds,
        CancellationTokenSource workerCts)
    {
        var clamped = Math.Clamp(
            leaseSeconds,
            LeaseStore.MinLeaseSeconds,
            LeaseStore.MaxLeaseSeconds);
        var interval = TimeSpan.FromSeconds(Math.Max(5, clamped / 2));
        var ct = workerCts.Token;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _delay(interval, ct);
                if (!_leases.Renew(taskId, runnerNid, clamped))
                {
                    if (!workerCts.IsCancellationRequested)
                        workerCts.Cancel();
                    return false;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal worker completion or host shutdown.
        }

        return true;
    }
}
