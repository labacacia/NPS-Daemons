// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.Daemon.Runner;

/// <summary>
/// NPS-CR-0007 §6 / §7 runtime error codes and terminal-state mapping for a finished worker.
/// </summary>
internal static class RunnerCodes
{
    public const string RuntimeIdleTimeout = "NOP-RUNTIME-IDLE-TIMEOUT";
    public const string RuntimeMaxRuntime = "NOP-RUNTIME-MAX-RUNTIME";

    /// <summary>
    /// Maps a <c>WorkerProcess</c> kill reason to its NPS-CR-0007 §6 error code, or null for a
    /// clean exit / shutdown (no error envelope).
    /// </summary>
    public static string? MapKilledReason(string? killedReason) => killedReason switch
    {
        "idle_timeout" => RuntimeIdleTimeout,
        "max_runtime" => RuntimeMaxRuntime,
        _ => null,
    };

    /// <summary>
    /// Terminal node state per NPS-5 §5: a clean exit (no kill, exit code 0) maps to
    /// <c>COMPLETED</c>; any kill or non-zero exit maps to <c>FAILED</c> (NPS-CR-0007 §6).
    /// </summary>
    public static string NodeState(int? exitCode, string? killedReason)
        => killedReason is null && exitCode == 0 ? "COMPLETED" : "FAILED";
}
