// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections;
using NPS.Core;
using NPS.Core.Caching;
using NPS.Core.Codecs;
using NPS.Core.Exceptions;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.Core.Ncp;
using NPS.Daemon.Npsd.SubNids;

namespace NPS.Daemon.Npsd.Native;

/// <summary>Hosts one bounded native NCP session after the router consumes its preamble.</summary>
internal sealed class NpsdNativeNcpSessionHost(
    NpsdOptions options,
    SubNidService nids,
    AnchorFrameCache anchors,
    ILogger<NpsdNativeNcpSessionHost> logger)
{
    private readonly NpsFrameCodec _codec = NpsFrameCodec.CreateDefault();

    internal async Task HandleAsync(ConnectionContext context)
    {
        var reader = context.Transport.Input;
        var writer = context.Transport.Output;
        var connectionClosed = context.ConnectionClosed;
        var handshakeAccepted = false;

        try
        {
            using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(connectionClosed);
            helloTimeout.CancelAfter(TimeSpan.FromMilliseconds(options.NcpHelloTimeoutMs));

            var helloWire = await ReadFrameAsync(
                reader,
                (uint)options.NcpMaxHelloPayloadBytes,
                helloTimeout.Token).ConfigureAwait(false);
            var helloHeader = NpsFrameCodec.PeekHeader(helloWire);
            if (helloHeader.FrameType != FrameType.Hello
                || helloHeader.EncodingTier != EncodingTier.Json
                || helloHeader.IsEncrypted
                || helloHeader.IsExtended)
            {
                return;
            }

            var hello = _codec.Decode(helloWire) as HelloFrame;
            if (hello is null)
                return;

            var profile = new NcpHandshakeProfile
            {
                SupportedEncodings = options.NcpEnableMsgPack
                    ? ["msgpack", "json"]
                    : ["json"],
                SupportedProtocols = ["ncp"],
                MaxConcurrentStreams = 1,
            };
            var negotiation = NcpNativeServerPolicy.Negotiate(profile, hello);
            if (negotiation.Action != NcpHandshakeAction.Accept)
            {
                await WriteErrorAsync(
                    writer,
                    negotiation.Status ?? NpsStatusCodes.ProtoVersionIncompatible,
                    negotiation.Error ?? NcpErrorCodes.VersionIncompatible,
                    "Native NCP handshake negotiation failed.",
                    connectionClosed).ConfigureAwait(false);
                return;
            }

            var tier = negotiation.NegotiatedEncoding == "msgpack"
                ? EncodingTier.MsgPack
                : EncodingTier.Json;
            var policy = new NcpEncodingPolicy(
                tier,
                negotiation.EnabledEncodings?.Contains("binary_vector.v1", StringComparer.Ordinal) == true);
            var caps = new NcpHandshakeCapsFrame
            {
                NodeId = $"{nids.HostNid}:npsd",
                Caps = ["ncp", "anchor-cache"],
                SessionVersion = negotiation.SessionVersion,
                NegotiatedEncoding = negotiation.NegotiatedEncoding,
                EnabledEncodings = negotiation.EnabledEncodings,
                SupportedProtocols = negotiation.SupportedProtocols,
                MaxFramePayload = negotiation.MaxFramePayload,
                ExtSupport = negotiation.ExtSupport,
                MaxConcurrentStreams = negotiation.MaxConcurrentStreams,
            };
            await writer.WriteAsync(_codec.Encode(caps, tier), connectionClosed).ConfigureAwait(false);
            handshakeAccepted = true;

            while (!connectionClosed.IsCancellationRequested)
            {
                byte[] wire;
                try
                {
                    wire = await ReadFrameAsync(
                        reader,
                        negotiation.MaxFramePayload ?? FrameHeader.DefaultMaxPayload,
                        connectionClosed).ConfigureAwait(false);
                }
                catch (EndOfStreamException)
                {
                    return;
                }

                var header = NpsFrameCodec.PeekHeader(wire);
                policy.EnsureAllows(header);
                if (_codec.Decode(wire) is not AnchorFrame anchor)
                {
                    await WriteErrorAsync(
                        writer,
                        NpsStatusCodes.ServerUnsupported,
                        NcpErrorCodes.FrameUnknownType,
                        $"npsd native L1 does not handle frame type 0x{(byte)header.FrameType:X2}.",
                        connectionClosed).ConfigureAwait(false);
                    return;
                }

                var computedAnchorId = AnchorFrameCache.ComputeAnchorId(anchor.Schema);
                if (!string.Equals(anchor.AnchorId, computedAnchorId, StringComparison.Ordinal))
                    throw new NpsFrameException(
                        "AnchorFrame anchor_id does not match its canonical schema digest.",
                        NpsStatusCodes.ClientConflict,
                        NcpErrorCodes.AnchorIdMismatch);

                var anchorId = anchors.Set(anchor);
                var ack = new CapsFrame
                {
                    AnchorRef = anchorId,
                    Count = 0,
                    Data = [],
                    Cached = true,
                };
                await writer.WriteAsync(_codec.Encode(ack, tier), connectionClosed).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (connectionClosed.IsCancellationRequested)
        {
            // Normal peer disconnect / host shutdown.
        }
        catch (OperationCanceledException)
        {
            // A slow/incomplete Hello is a silent pre-admission close.
        }
        catch (NpsException) when (!handshakeAccepted)
        {
            // Malformed pre-admission framing closes silently; only a valid
            // Hello with incompatible negotiation receives an ErrorFrame.
        }
        catch (NpsException ex)
        {
            await TryWriteErrorAsync(writer, ex, connectionClosed).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Native NCP connection closed after an invalid frame.");
            if (!handshakeAccepted)
                return;
            await TryWriteErrorAsync(
                writer,
                new NpsFrameException(
                    "Invalid native NCP frame.",
                    NpsStatusCodes.ClientBadFrame,
                    NcpErrorCodes.FrameUnknownType),
                connectionClosed).ConfigureAwait(false);
        }
    }

    private static async Task<byte[]> ReadFrameAsync(
        PipeReader reader,
        uint maxPayload,
        CancellationToken cancellationToken)
    {
        var first = await ReadExactlyAsync(reader, 2, cancellationToken).ConfigureAwait(false);
        var extended = ((FrameFlags)first[1] & FrameFlags.Ext) != 0;
        var headerSize = extended ? FrameHeader.ExtendedSize : FrameHeader.DefaultSize;
        var rest = await ReadExactlyAsync(reader, headerSize - first.Length, cancellationToken).ConfigureAwait(false);
        var headerBytes = new byte[headerSize];
        first.CopyTo(headerBytes, 0);
        rest.CopyTo(headerBytes, first.Length);
        var header = FrameHeader.Parse(headerBytes);
        if (header.PayloadLength > maxPayload)
            throw new NpsFrameException(
                $"Frame payload {header.PayloadLength} exceeds negotiated maximum {maxPayload}.",
                NpsStatusCodes.LimitPayload,
                NcpErrorCodes.FramePayloadTooLarge);

        var payload = await ReadExactlyAsync(reader, checked((int)header.PayloadLength), cancellationToken)
            .ConfigureAwait(false);
        var wire = new byte[headerSize + payload.Length];
        headerBytes.CopyTo(wire, 0);
        payload.CopyTo(wire, headerSize);
        return wire;
    }

    private static async Task<byte[]> ReadExactlyAsync(
        PipeReader reader,
        int length,
        CancellationToken cancellationToken)
    {
        if (length == 0)
            return [];

        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;
            if (buffer.Length >= length)
            {
                var bytes = new byte[length];
                buffer.Slice(0, length).CopyTo(bytes);
                var consumed = buffer.GetPosition(length);
                reader.AdvanceTo(consumed, consumed);
                return bytes;
            }

            if (result.IsCompleted)
            {
                reader.AdvanceTo(buffer.End);
                throw new EndOfStreamException("Native NCP peer closed mid-frame.");
            }

            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    private Task TryWriteErrorAsync(PipeWriter writer, NpsException exception, CancellationToken ct) =>
        WriteErrorAsync(
            writer,
            exception.NpsStatusCode ?? NpsStatusCodes.ClientBadFrame,
            exception.ProtocolErrorCode ?? NcpErrorCodes.FrameUnknownType,
            exception.Message,
            ct,
            swallowFailure: true);

    private async Task WriteErrorAsync(
        PipeWriter writer,
        string status,
        string error,
        string message,
        CancellationToken ct,
        bool swallowFailure = false)
    {
        try
        {
            await writer.WriteAsync(_codec.Encode(new ErrorFrame
            {
                Status = status,
                Error = error,
                Message = message,
            }, EncodingTier.Json), ct).ConfigureAwait(false);
        }
        catch when (swallowFailure)
        {
            // Peer already closed; there is nowhere to report the protocol error.
        }
    }
}
