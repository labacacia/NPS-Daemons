// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace NPS.Daemon.Runner;

/// <summary>
/// Manages the lifecycle of a single spawned worker subprocess.
/// </summary>
internal sealed class WorkerProcess(SpawnSpec spec, string logPath, RunnerOptions opts, ILogger log)
{
    public string TaskId => spec.TaskId;

    /// <summary>
    /// Starts the process and waits for it to exit.
    /// Returns the exit code, or null if the process was killed.
    /// Also returns the kill reason (null = clean exit).
    /// </summary>
    public async Task<(int? ExitCode, string? KilledReason)> RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        var psi = BuildStartInfo();

        await using var logWriter = new StreamWriter(logPath, append: false, System.Text.Encoding.UTF8)
        {
            AutoFlush = true,
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var exitTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedAt = DateTimeOffset.UtcNow;
        // Store last-output time as UTC ticks for lock-free access across threads.
        long lastOutputTicks = startedAt.UtcTicks;
        var killReason = (string?)null;

        process.Exited += (_, _) => exitTcs.TrySetResult(process.ExitCode);
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                logWriter.WriteLine($"[stdout] {e.Data}");
                Interlocked.Exchange(ref lastOutputTicks, DateTimeOffset.UtcNow.UtcTicks);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                logWriter.WriteLine($"[stderr] {e.Data}");
                Interlocked.Exchange(ref lastOutputTicks, DateTimeOffset.UtcNow.UtcTicks);
            }
        };

        log.LogInformation("Worker {TaskId}: spawning {Command} {Args}",
            spec.TaskId, psi.FileName, string.Join(" ", psi.ArgumentList));
        logWriter.WriteLine($"[runner] spawned at {startedAt:O}  command={psi.FileName}  args={string.Join(" ", psi.ArgumentList)}");

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start process: {psi.FileName}");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var maxRuntime = spec.MaxRuntimeSeconds.HasValue
            ? TimeSpan.FromSeconds(spec.MaxRuntimeSeconds.Value)
            : TimeSpan.FromHours(4);

        // Monitor loop: checks idle and max-runtime every 5 s.
        using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitorTask = Task.Run(async () =>
        {
            while (!exitTcs.Task.IsCompleted)
            {
                try { await Task.Delay(5_000, monitorCts.Token); }
                catch (OperationCanceledException) { return; }

                var observedAt = DateTimeOffset.UtcNow;
                var lastTicks = Interlocked.Read(ref lastOutputTicks);
                var reason = RunnerLifecyclePolicy.BreachReason(
                    observedAt - startedAt,
                    observedAt - new DateTimeOffset(lastTicks, TimeSpan.Zero),
                    spec.IdleTimeoutSeconds,
                    checked((int)maxRuntime.TotalSeconds));
                if (reason == "max_runtime")
                {
                    logWriter.WriteLine("[runner] max_runtime_seconds exceeded — killing");
                    log.LogWarning("Worker {TaskId}: max runtime exceeded, killing", spec.TaskId);
                    killReason = "max_runtime";
                    KillSafe(process);
                    return;
                }

                if (reason == "idle_timeout" && spec.IdleTimeoutSeconds is { } idleSec)
                {
                    var idleFor = (observedAt - new DateTimeOffset(lastTicks, TimeSpan.Zero)).TotalSeconds;
                    logWriter.WriteLine($"[runner] idle_timeout_seconds={idleSec} exceeded ({idleFor:F0}s since last output) — killing");
                    log.LogWarning("Worker {TaskId}: idle timeout ({IdleSec}s), killing", spec.TaskId, idleSec);
                    killReason = "idle_timeout";
                    KillSafe(process);
                    return;
                }
            }
        }, monitorCts.Token);

        int? exitCode;
        try
        {
            exitCode = await exitTcs.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            logWriter.WriteLine("[runner] shutdown signal — killing");
            killReason = "shutdown";
            KillSafe(process);
            exitCode = null;
        }
        finally
        {
            await monitorCts.CancelAsync();
        }

        try { await monitorTask; }
        catch (OperationCanceledException) { /* expected on clean exit */ }
        if (killReason is not null)
            exitCode = null;

        logWriter.WriteLine($"[runner] finished at {DateTimeOffset.UtcNow:O}  exit_code={exitCode}  killed={killReason ?? "none"}");
        log.LogInformation("Worker {TaskId}: exit_code={ExitCode} killed={KilledReason}",
            spec.TaskId, exitCode, killReason ?? "none");

        return (exitCode, killReason);
    }

    private ProcessStartInfo BuildStartInfo()
    {
        var info = new ProcessStartInfo
        {
            FileName = spec.IsPortableOci ? opts.OciRuntime : spec.Command!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            WorkingDirectory = spec.WorkDir ?? Directory.GetCurrentDirectory(),
        };

        if (!spec.IsPortableOci)
        {
            foreach (var arg in spec.Args)
                info.ArgumentList.Add(arg);
            foreach (var (key, value) in spec.Env)
                info.Environment[key] = value;
            return info;
        }

        info.ArgumentList.Add("run");
        info.ArgumentList.Add("--rm");
        info.ArgumentList.Add("--name");
        info.ArgumentList.Add($"nps-runner-{SafeContainerName(spec.TaskId)}");
        foreach (var (key, value) in spec.Env)
        {
            info.ArgumentList.Add("--env");
            info.ArgumentList.Add($"{key}={value}");
        }
        if (spec.ResourceLimits?.Cpu is { Length: > 0 } cpu)
        {
            info.ArgumentList.Add("--cpus");
            info.ArgumentList.Add(NormalizeCpu(cpu));
        }
        if (spec.ResourceLimits?.Memory is { Length: > 0 } memory)
        {
            info.ArgumentList.Add("--memory");
            info.ArgumentList.Add(memory);
        }
        if (spec.ResourceLimits?.CognBudget is { } budget)
        {
            info.ArgumentList.Add("--env");
            info.ArgumentList.Add($"NPS_CGN_BUDGET={budget}");
        }
        info.ArgumentList.Add(spec.Image!);
        foreach (var item in spec.ContainerCommand)
            info.ArgumentList.Add(item);
        return info;
    }

    private static string NormalizeCpu(string value)
    {
        if (!value.EndsWith('m') ||
            !double.TryParse(
                value[..^1],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var millicores))
        {
            return value;
        }
        return (millicores / 1000d).ToString(
            "0.###",
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string SafeContainerName(string taskId)
    {
        var value = new string(taskId
            .ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')
            .ToArray());
        return value.Length <= 48 ? value : value[..48];
    }

    private static void KillSafe(Process p)
    {
        try { p.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }
}
