// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;

namespace NPS.Daemon.Runner;

/// <summary>
/// Enforces the concurrent-worker cap and fires off worker tasks asynchronously.
/// Each worker owns its inbox message until it exits, then acks and optionally notifies.
/// </summary>
internal sealed class WorkerManager(
    RunnerOptions opts,
    NpsdClient client,
    LeaseStore leases,
    LeaseRenewalLoop leaseRenewal,
    ILogger<WorkerManager> log)
{
    private readonly SemaphoreSlim _slots =
        new(opts.MaxConcurrentWorkers, opts.MaxConcurrentWorkers);

    public bool HasCapacity => _slots.CurrentCount > 0;

    /// <summary>
    /// Acquires a worker slot (non-blocking) and fires the worker task.
    /// Returns false if the concurrency cap is already reached
    /// (message should remain unacked for the next poll cycle).
    /// The caller has already claimed a durable task lease (NPS-CR-0007 §4).
    /// <paramref name="dedupKey"/> fences terminal completion (§4.3).
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

        // NPS-CR-0007 §4.2: periodically renew the durable task lease while the
        // worker runs. If ownership is lost, cancel the worker and abandon completion.
        using var workerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var leaseSeconds = Math.Clamp(spec.MaxRuntimeSeconds ?? LeaseStore.MaxLeaseSeconds,
            LeaseStore.MinLeaseSeconds, LeaseStore.MaxLeaseSeconds);
        var renewLoop = leaseRenewal.RunAsync(spec.TaskId, runnerNid, leaseSeconds, workerCts);

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
                            "Worker {TaskId}: durable lease ownership lost — abandoning completion.",
                            spec.TaskId);
                    }
                }
            }
            catch (OperationCanceledException) when (workerCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // The renewal loop lost the durable lease and cancelled us. Abandon
                // without committing terminal state, acking, or notifying.
                abandonCompletion = true;
                killedReason = "lease-lost";
                log.LogWarning(
                    "Worker {TaskId}: durable lease ownership lost — abandoning completion.",
                    spec.TaskId);
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
            var retainedLease = await renewLoop;
            if (!retainedLease)
            {
                log.LogWarning(
                    "Worker {TaskId}: durable lease renewal failed — worker cancellation requested.",
                    spec.TaskId);
            }
            _slots.Release();

            // On lease loss, do NOT write terminal state, ack, or notify. Otherwise atomically
            // commit the durable terminal record and release the lease before external effects.
            if (!abandonCompletion)
            {
                if (leases.TryCompleteTask(spec.TaskId, runnerNid, dedupKey))
                {
                    // Ack only after the durable terminal commit. If ack fails, the next
                    // delivery observes AlreadyCompleted and retries the idempotent ack.
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
                else
                {
                    log.LogWarning(
                        "Worker {TaskId}: terminal commit rejected after lease loss/expiry — leaving inbox unacked.",
                        spec.TaskId);
                }
            }
        }
    }

}
