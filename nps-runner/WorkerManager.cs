// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;

namespace NPS.Daemon.Runner;

/// <summary>
/// Enforces the concurrent-worker cap and fires off worker tasks asynchronously.
/// Each worker owns its inbox message until it exits, then acks and optionally notifies.
/// </summary>
internal sealed class WorkerManager(RunnerOptions opts, NpsdClient client, LeaseStore leases, ILogger<WorkerManager> log)
{
    private readonly SemaphoreSlim _slots =
        new(opts.MaxConcurrentWorkers, opts.MaxConcurrentWorkers);

    public bool HasCapacity => _slots.CurrentCount > 0;

    /// <summary>
    /// Acquires a worker slot (non-blocking) and fires the worker task.
    /// Returns false if the concurrency cap is already reached
    /// (message should remain unacked for the next poll cycle).
    /// The caller has already claimed the task lease (NPS-CR-0007 §4); <paramref name="dedupKey"/>
    /// is the lease's dedup key, used to release the lease and mark the node terminal on
    /// completion (§4.3) so a reclaiming runner does not re-execute it.
    /// </summary>
    public bool TrySpawn(SpawnSpec spec, string runnerNid, string messageId, string dedupKey, CancellationToken ct)
    {
        if (!_slots.Wait(0))
            return false;

        _ = RunWorkerAsync(spec, runnerNid, messageId, dedupKey, ct);
        return true;
    }

    private async Task RunWorkerAsync(SpawnSpec spec, string runnerNid, string messageId, string dedupKey, CancellationToken ct)
    {
        // Everything below runs inside the try so the slot is released even if worker construction
        // or the renewal-loop setup throws synchronously (otherwise the slot — and the discarded
        // Task's exception — would leak, permanently shrinking capacity).
        int? exitCode = null;
        string? killedReason = null;
        bool abandonCompletion = false;
        var startedAt = DateTimeOffset.UtcNow;
        var logPath = Path.Combine(opts.LogDir, $"{spec.TaskId}.log");

        // NPS-CR-0007 §4.2: renew the task lease while the worker runs so a long-running task is
        // not reclaimed by another runner mid-execution. If a renewal finds the lease gone (the
        // task was reclaimed after our lease expired), cancel the worker so it does not keep
        // executing a task another runner now owns (exactly-once, §4.2).
        using var workerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var leaseSeconds = Math.Clamp(spec.MaxRuntimeSeconds ?? LeaseStore.MaxLeaseSeconds,
            LeaseStore.MinLeaseSeconds, LeaseStore.MaxLeaseSeconds);
        var renewLoop = RenewLeaseAsync(spec.TaskId, runnerNid, leaseSeconds, workerCts);

        try
        {
            var worker = new WorkerProcess(spec, logPath, opts, log);
            try
            {
                (exitCode, killedReason) = await worker.RunAsync(workerCts.Token);
                if (string.Equals(killedReason, "shutdown", StringComparison.Ordinal))
                {
                    abandonCompletion = true;
                    if (!ct.IsCancellationRequested)
                    {
                        killedReason = "lease-lost";
                        log.LogWarning(
                            "Worker {TaskId}: lease lost/reclaimed mid-execution — abandoning to the reclaiming runner.",
                            spec.TaskId);
                    }
                }
            }
            catch (OperationCanceledException) when (workerCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // The renewal loop lost the lease and cancelled us: another runner has reclaimed the
                // task. Abandon WITHOUT marking the node terminal so the new owner is free to run it.
                abandonCompletion = true;
                killedReason = "lease-lost";
                log.LogWarning("Worker {TaskId}: lease lost/reclaimed mid-execution — abandoning to the reclaiming runner.", spec.TaskId);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Worker {TaskId}: unhandled exception in RunAsync", spec.TaskId);
                killedReason = "exception";
            }
        }
        finally
        {
            workerCts.Cancel();
            try { await renewLoop; } catch (OperationCanceledException) { }
            _slots.Release();

            // On lease loss, do NOT mark the node terminal, ack, or notify: the lease (and the
            // inbox message) belong to the reclaiming runner now. Release is a no-op (we no longer
            // own it). Otherwise complete normally per NPS-CR-0007 §4.3.
            if (!abandonCompletion)
            {
                // NPS-CR-0007 §4.3: record the node terminal (dedup) and release the lease so the
                // task is not re-executed on reclaim and the lease frees immediately.
                leases.MarkNodeDone(dedupKey, spec.TaskId);
                leases.Release(spec.TaskId, runnerNid);

                // Ack the inbox message now that the worker has finished.
                try { await client.AckAsync(runnerNid, messageId, CancellationToken.None); }
                catch (Exception ex) { log.LogWarning(ex, "Worker {TaskId}: ack failed (message_id={MsgId})", spec.TaskId, messageId); }

                // Best-effort completion notification.
                if (spec.ReplyTo is not null)
                {
                    var note = new CompletionNotification
                    {
                        TaskId = spec.TaskId,
                        ExitCode = exitCode,
                        KilledReason = killedReason,
                        ErrorCode = RunnerCodes.MapKilledReason(killedReason),
                        NodeState = RunnerCodes.NodeState(exitCode, killedReason),
                        LogPath = logPath,
                        StartedAt = startedAt.ToString("O"),
                        FinishedAt = DateTimeOffset.UtcNow.ToString("O"),
                    };
                    try { await client.NotifyAsync(spec.ReplyTo, note, CancellationToken.None); }
                    catch (Exception ex) { log.LogWarning(ex, "Worker {TaskId}: completion notify failed (reply_to={ReplyTo})", spec.TaskId, spec.ReplyTo); }
                }
            }
        }
    }

    /// <summary>
    /// Renews the task lease at half its window (NPS-CR-0007 §4.2), keeping a long-running worker's
    /// claim alive. If a renewal finds the lease is no longer ours — it expired and another runner
    /// reclaimed the task — it cancels <paramref name="workerCts"/> so the worker stops rather than
    /// double-executing the reclaimed task.
    /// </summary>
    private async Task RenewLeaseAsync(string taskId, string runnerNid, int leaseSeconds, CancellationTokenSource workerCts)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, leaseSeconds / 2));
        var ct = workerCts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct);
                if (!leases.Renew(taskId, runnerNid, leaseSeconds))
                {
                    log.LogWarning("Worker {TaskId}: lease renewal failed (lost/reclaimed) — cancelling worker.", taskId);
                    if (!workerCts.IsCancellationRequested)
                        workerCts.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { /* worker finished — stop renewing */ }
    }
}
