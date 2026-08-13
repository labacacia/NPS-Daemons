// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.Daemon.Runner;

internal static class RunnerLifecyclePolicy
{
    /// <summary>
    /// Returns the runner kill reason. Max runtime takes precedence when both
    /// limits are reached in the same observation.
    /// </summary>
    public static string? BreachReason(
        TimeSpan elapsed,
        TimeSpan idle,
        int? idleTimeoutSeconds,
        int maxRuntimeSeconds)
    {
        if (elapsed >= TimeSpan.FromSeconds(maxRuntimeSeconds))
            return "max_runtime";
        if (idleTimeoutSeconds is { } idleSeconds &&
            idle >= TimeSpan.FromSeconds(idleSeconds))
        {
            return "idle_timeout";
        }
        return null;
    }
}
