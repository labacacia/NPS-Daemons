// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.Daemon.Runner;

internal enum ClaimDisposition
{
    Spawn,
    AckDuplicate,
    LeaveUnacked,
}

internal static class RunnerClaimPolicy
{
    public static ClaimDisposition DispositionFor(ClaimResult result) => result switch
    {
        ClaimResult.Granted or ClaimResult.Reclaimed => ClaimDisposition.Spawn,
        ClaimResult.AlreadyCompleted => ClaimDisposition.AckDuplicate,
        ClaimResult.Conflict => ClaimDisposition.LeaveUnacked,
        _ => throw new ArgumentOutOfRangeException(nameof(result), result, null),
    };
}
