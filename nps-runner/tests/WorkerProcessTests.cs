// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace NPS.Daemon.Runner.Tests;

public sealed class WorkerProcessTests
{
    [Fact]
    public async Task Cancellation_kills_worker_and_returns_shutdown_without_exit_code()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"nps-runner-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var spec = new SpawnSpec
            {
                TaskId = "cancelled-worker",
                Command = "/bin/sh",
                Args = ["-c", "while true; do sleep 1; done"],
                MaxRuntimeSeconds = 60,
            };
            var options = new RunnerOptions { LogDir = root };
            var worker = new WorkerProcess(
                spec,
                Path.Combine(root, "worker.log"),
                options,
                NullLogger.Instance);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

            var result = await worker.RunAsync(cts.Token);

            Assert.Null(result.ExitCode);
            Assert.Equal("shutdown", result.KilledReason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
