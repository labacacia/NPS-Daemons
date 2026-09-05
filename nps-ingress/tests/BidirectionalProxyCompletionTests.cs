// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.Daemon.Ingress;
using Xunit;

namespace NPS.Daemon.Ingress.Tests;

public sealed class BidirectionalProxyCompletionTests
{
    [Fact]
    public async Task Client_half_close_signals_backend_then_drains_remaining_response()
    {
        var response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backendSendCompleted = false;

        var completion = BidirectionalProxyCompletion.AwaitAsync(
            Task.CompletedTask,
            response.Task,
            () => backendSendCompleted = true);

        await Task.Yield();
        Assert.True(backendSendCompleted);
        Assert.False(completion.IsCompleted);

        response.SetResult();
        await completion;
    }

    [Fact]
    public async Task Backend_close_does_not_signal_a_client_half_close()
    {
        var request = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backendSendCompleted = false;

        var completion = BidirectionalProxyCompletion.AwaitAsync(
            request.Task,
            Task.CompletedTask,
            () => backendSendCompleted = true);

        await Task.Yield();
        Assert.False(backendSendCompleted);
        Assert.False(completion.IsCompleted);

        request.SetResult();
        await completion;
    }
}
