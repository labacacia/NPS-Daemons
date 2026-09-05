// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.Daemon.Npsd.Inbox;

/// <summary>
/// One queued inbox message addressed to a sub-NID.
/// </summary>
public sealed record InboxMessage
{
    /// <summary>Server-assigned monotonic message id from the durable inbox database.</summary>
    public required ulong MessageId { get; init; }

    /// <summary>Recipient sub-NID.</summary>
    public required string Nid { get; init; }

    /// <summary>UTC enqueue time.</summary>
    public required DateTimeOffset EnqueuedAt { get; init; }

    /// <summary>UTC expiry — message is silently dropped past this time.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// Priority hint. Higher numbers drain first. Default 0. Producers
    /// declare priority; npsd does not enforce any meaning beyond ordering.
    /// </summary>
    public required int Priority { get; init; }

    /// <summary>Opaque payload. Up to <see cref="NpsdOptions.MaxInboxMessageBytes"/>.</summary>
    public required byte[] Payload { get; init; }

    /// <summary>
    /// Caller-declared content type for the payload, e.g. <c>application/nwp+msgpack</c>
    /// or <c>application/json</c>. Free-form; npsd only stores it.
    /// </summary>
    public required string ContentType { get; init; }
}
