// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using Microsoft.AspNetCore.Connections;
using NPS.Core.Ncp;

namespace NPS.Daemon.Npsd.Native;

/// <summary>
/// Losslessly distinguishes HTTP from native NCP on the unified suite port.
/// HTTP bytes remain unconsumed for Kestrel; a valid NCP preamble is consumed
/// before control passes to the native session host.
/// </summary>
internal static class NpsdConnectionRouter
{
    internal static async Task RouteAsync(
        ConnectionContext context,
        ConnectionDelegate next,
        NpsdOptions options,
        Func<NpsdNativeNcpSessionHost> getNativeHost)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.ConnectionClosed);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(options.NcpPreambleTimeoutMs));

        var classification = await ClassifyAsync(context, timeout.Token).ConfigureAwait(false);
        switch (classification)
        {
            case ConnectionKind.Http:
                await next(context).ConfigureAwait(false);
                return;
            case ConnectionKind.NativeNcp:
                await getNativeHost().HandleAsync(context).ConfigureAwait(false);
                return;
            default:
                // Native-looking invalid/incomplete preambles close silently.
                return;
        }
    }

    private static async Task<ConnectionKind> ClassifyAsync(
        ConnectionContext context,
        CancellationToken cancellationToken)
    {
        var reader = context.Transport.Input;
        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buffer = result.Buffer;
                var inspectedLength = (int)Math.Min(buffer.Length, NcpPreamble.Length);
                var inspected = new byte[inspectedLength];
                buffer.Slice(0, inspectedLength).CopyTo(inspected);

                var matched = 0;
                while (matched < inspected.Length && inspected[matched] == NcpPreamble.Bytes[matched])
                    matched++;

                if (matched < inspected.Length)
                {
                    // Only an NPS-looking prefix is treated as a malformed native
                    // preamble. Everything else is returned untouched to HTTP.
                    reader.AdvanceTo(buffer.Start, buffer.Start);
                    return matched >= 3
                        ? ConnectionKind.InvalidNativeNcp
                        : ConnectionKind.Http;
                }

                if (inspectedLength == NcpPreamble.Length)
                {
                    var consumed = buffer.GetPosition(NcpPreamble.Length);
                    reader.AdvanceTo(consumed, consumed);
                    return ConnectionKind.NativeNcp;
                }

                if (result.IsCompleted)
                {
                    reader.AdvanceTo(buffer.End);
                    return ConnectionKind.InvalidNativeNcp;
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
            }
        }
        catch (OperationCanceledException)
        {
            return ConnectionKind.InvalidNativeNcp;
        }
    }

    private enum ConnectionKind
    {
        Http,
        NativeNcp,
        InvalidNativeNcp,
    }
}
