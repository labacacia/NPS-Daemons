// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NPS.Daemon.Runner;

/// <summary>
/// Background service that:
/// 1. Self-registers with the local npsd at startup (idempotent).
/// 2. Long-polls the runner's inbox for spawn-spec messages.
/// 3. Dispatches each message to <see cref="WorkerManager"/>.
/// </summary>
internal sealed class InboxWatcher(
    RunnerOptions opts,
    NpsdClient client,
    SpawnSpecResolver resolver,
    WorkerManager workers,
    LeaseStore leases,
    ILogger<InboxWatcher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var runnerNid = await RegisterWithRetryAsync(ct);
        log.LogInformation(
            "nps-runner ready — NID={RunnerNid}  npsd={NpsdUrl}  max_workers={MaxWorkers}  log_dir={LogDir}",
            runnerNid, opts.NpsdUrl, opts.MaxConcurrentWorkers, opts.LogDir);

        // long-poll waits up to PollIntervalMs; minimum 1 s to avoid tight loops.
        var waitSec = Math.Max(1, opts.PollIntervalMs / 1000);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var msgs = await client.PollAsync(runnerNid, waitSec, batch: 16, ct);

                foreach (var msg in msgs)
                {
                    if (!string.Equals(msg.ContentType, "application/json",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        log.LogWarning(
                            "Inbox message {MsgId}: unsupported content_type={CT} — acking and skipping",
                            msg.MessageId, msg.ContentType);
                        await AckSafeAsync(runnerNid, msg.MessageId, ct);
                        continue;
                    }

                    SpawnSpec? spec;
                    try
                    {
                        var json = Encoding.UTF8.GetString(Convert.FromBase64String(msg.PayloadB64));
                        var resolution = await resolver.ResolveEnvelopeAsync(json, ct);
                        if (!resolution.Success)
                        {
                            log.LogWarning(
                                "Inbox message {MsgId}: invalid spawn spec ({ErrorCode}) — acking and skipping",
                                msg.MessageId, resolution.ErrorCode);
                            await AckSafeAsync(runnerNid, msg.MessageId, ct);
                            continue;
                        }
                        spec = resolution.Spec;
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning(ex,
                            "Inbox message {MsgId}: failed to parse spawn spec — acking and skipping",
                            msg.MessageId);
                        await AckSafeAsync(runnerNid, msg.MessageId, ct);
                        continue;
                    }

                    if (spec is null)
                    {
                        log.LogWarning(
                            "Inbox message {MsgId}: null spawn spec — acking and skipping",
                            msg.MessageId);
                        await AckSafeAsync(runnerNid, msg.MessageId, ct);
                        continue;
                    }

                    // NPS-CR-0007 §4: claim the task lease before spawning so two runners never
                    // execute the same task. The dedup key guards re-execution on reclaim (§4.3).
                    var dedupKey = LeaseStore.ComputeDedupKey(spec.TaskId, DagHashOf(spec));

                    if (leases.IsNodeDone(dedupKey, spec.TaskId))
                    {
                        log.LogInformation(
                            "Inbox message {MsgId} (task={TaskId}): already completed — acking duplicate",
                            msg.MessageId, spec.TaskId);
                        await AckSafeAsync(runnerNid, msg.MessageId, ct);
                        continue;
                    }

                    var leaseSeconds = spec.MaxRuntimeSeconds ?? LeaseStore.MaxLeaseSeconds;
                    var claim = leases.TryClaim(spec.TaskId, runnerNid, leaseSeconds, dedupKey);
                    if (claim.Result == ClaimResult.Conflict)
                    {
                        log.LogInformation(
                            "Inbox message {MsgId} (task={TaskId}): claimed by another runner ({Err}) — acking",
                            msg.MessageId, spec.TaskId, claim.ErrorCode);
                        await AckSafeAsync(runnerNid, msg.MessageId, ct);
                        continue;
                    }

                    if (!workers.TrySpawn(spec, runnerNid, msg.MessageId, dedupKey, ct))
                    {
                        // Concurrency cap reached; release the lease so another runner can take it,
                        // and leave the message unacked so it re-appears next poll.
                        leases.Release(spec.TaskId, runnerNid);
                        log.LogDebug(
                            "Inbox message {MsgId} (task={TaskId}): concurrency cap reached, will retry",
                            msg.MessageId, spec.TaskId);
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "Inbox poll error — retrying in {PollMs} ms", opts.PollIntervalMs);
                try { await Task.Delay(opts.PollIntervalMs, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        log.LogInformation("nps-runner shutting down");
    }

    private async Task<string> RegisterWithRetryAsync(CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await client.EnsureRegisteredAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (attempt < 20)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(30, attempt * 2));
                log.LogWarning(ex,
                    "Registration attempt {Attempt} failed — retrying in {Delay}",
                    attempt, delay);
                await Task.Delay(delay, ct);
            }
        }
    }

    private async Task AckSafeAsync(string nid, string messageId, CancellationToken ct)
    {
        try { await client.AckAsync(nid, messageId, ct); }
        catch (Exception ex) { log.LogWarning(ex, "Ack failed for message {MsgId}", messageId); }
    }

    /// <summary>
    /// Stable digest of a spawn spec, used as the <c>dag_hash</c> component of the dedup key
    /// (NPS-CR-0007 §4.1). For the subprocess runtime, the spawn command + args identify the
    /// unit of work; two byte-identical specs for the same task_id are the same work.
    /// </summary>
    private static string DagHashOf(SpawnSpec spec)
    {
        var canonical = spec.IsPortableOci
            ? spec.Image + "\n" + string.Join("\0", spec.ContainerCommand)
            : spec.Command + "\n" + string.Join("\0", spec.Args);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16]
            .ToLowerInvariant();
    }
}
