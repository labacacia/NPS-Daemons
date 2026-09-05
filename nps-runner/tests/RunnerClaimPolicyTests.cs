// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.Daemon.Runner;
using Xunit;

namespace NPS.Daemon.Runner.Tests;

public sealed class RunnerClaimPolicyTests
{
    [Theory]
    [InlineData((int)ClaimResult.Granted, (int)ClaimDisposition.Spawn)]
    [InlineData((int)ClaimResult.Reclaimed, (int)ClaimDisposition.Spawn)]
    [InlineData((int)ClaimResult.AlreadyCompleted, (int)ClaimDisposition.AckDuplicate)]
    [InlineData((int)ClaimResult.Conflict, (int)ClaimDisposition.LeaveUnacked)]
    public void Claim_outcome_controls_spawn_and_ack_without_fail_open(
        int result,
        int expected)
    {
        Assert.Equal(
            (ClaimDisposition)expected,
            RunnerClaimPolicy.DispositionFor((ClaimResult)result));
    }
}
