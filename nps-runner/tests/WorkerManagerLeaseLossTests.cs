// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace NPS.Daemon.Runner.Tests;

public sealed class WorkerManagerLeaseLossTests
{
    [Fact]
    public async Task Reclaimed_lease_cancels_stale_worker_without_ack_or_notification()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"nps-runner-manager-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var now = DateTimeOffset.UnixEpoch;
        var database = $"lease-manager-{Guid.NewGuid():N}";
        using var stale = LeaseStore.CreateSharedInMemoryForTests(
            database,
            "instance-stale",
            () => now);
        using var replacement = LeaseStore.CreateSharedInMemoryForTests(
            database,
            "instance-replacement",
            () => now);
        var delayEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRenewal = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var options = new RunnerOptions
        {
            LogDir = root,
            MaxConcurrentWorkers = 1,
        };
        var client = new NpsdClient(http, options);
        var renewal = new LeaseRenewalLoop(stale, async (_, ct) =>
        {
            delayEntered.TrySetResult();
            await allowRenewal.Task.WaitAsync(ct);
        });
        var manager = new WorkerManager(
            options,
            client,
            stale,
            renewal,
            NullLogger<WorkerManager>.Instance);
        var spec = new SpawnSpec
        {
            TaskId = "stale-worker",
            Command = "/bin/sh",
            Args = ["-c", "while true; do sleep 1; done"],
            MaxRuntimeSeconds = 10,
            ReplyTo = "urn:nps:agent:test:reply",
        };
        var dedup = LeaseStore.ComputeDedupKey(spec.TaskId, "dag");
        Assert.Equal(
            ClaimResult.Granted,
            stale.TryClaim(spec.TaskId, "runner", 10, dedup).Result);

        try
        {
            Assert.True(manager.TrySpawn(spec, "runner", "message-1", dedup, CancellationToken.None));
            Assert.False(manager.HasCapacity);
            await delayEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            now = now.AddSeconds(11);
            Assert.Equal(
                ClaimResult.Reclaimed,
                replacement.TryClaim(spec.TaskId, "runner", 10, dedup).Result);
            allowRenewal.TrySetResult();

            await WaitUntilAsync(() => manager.HasCapacity, TimeSpan.FromSeconds(10));

            Assert.Empty(handler.Requests);
            Assert.False(stale.IsNodeDone(dedup, spec.TaskId));
            Assert.False(stale.TryCompleteTask(spec.TaskId, "runner", dedup));
        }
        finally
        {
            allowRenewal.TrySetResult();
            await WaitUntilAsync(() => manager.HasCapacity, TimeSpan.FromSeconds(10));
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("Timed out waiting for the worker to stop.");
            await Task.Delay(25);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri? Uri)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }
}
