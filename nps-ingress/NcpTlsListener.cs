// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NPS.Core;
using NPS.Core.Codecs;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.Core.Ncp;

namespace NPS.Daemon.Ingress;

/// <summary>
/// Layer-2 native-mode TLS terminator (NPS-RFC-0006 §6). Accepts NCP-over-TLS connections,
/// negotiating ALPN <c>nps/1.0</c> over TLS 1.3 with mutual authentication: the client
/// certificate is validated to a configured trust anchor and its NID bound to the session
/// (<see cref="NipMtlsValidator"/>). The terminated NCP byte stream is then proxied to the local
/// backend (npsd). Stays idle unless a server certificate is configured.
/// </summary>
internal sealed class NcpTlsListener(IngressOptions opts, ILogger<NcpTlsListener> log) : BackgroundService
{
    private static readonly NpsFrameCodec HandshakeCodec = NpsFrameCodec.CreateDefault();
    private X509Certificate2? _serverCert;
    private List<X509Certificate2> _trustAnchors = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (opts.TlsPort == 0 || string.IsNullOrWhiteSpace(opts.ServerCertPath))
        {
            log.LogWarning(
                "nps-ingress L2 TLS listener disabled (tls_port={Port}, cert={Cert}). " +
                "Set NPSINGRESS_CERT_PATH to enable NCP-over-TLS termination.",
                opts.TlsPort, opts.ServerCertPath ?? "<none>");
            return;
        }

        try
        {
            _serverCert = X509CertificateLoader.LoadPkcs12FromFile(opts.ServerCertPath!, opts.ServerCertPassword);
            _trustAnchors = LoadTrustAnchors();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "nps-ingress L2: failed to load TLS material — listener not started.");
            return;
        }

        var listener = new TcpListener(IPAddress.Any, opts.TlsPort);
        listener.Start();
        log.LogInformation(
            "nps-ingress L2 TLS listener up on :{Port} (ALPN {Alpn}, mTLS={Mtls}, trust_anchors={Anchors}) → backend {Host}:{BackendPort}",
            opts.TlsPort, NcpL2.Alpn, opts.RequireClientCert, _trustAnchors.Count, opts.BackendHost, opts.BackendPort);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(ct); }
                catch (OperationCanceledException) { break; }
                _ = HandleConnectionAsync(client, ct);
            }
        }
        finally { listener.Stop(); }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        using var _client = client;
        string? boundNid = null;
        var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        try
        {
            var authOpts = new SslServerAuthenticationOptions
            {
                ServerCertificate = _serverCert,
                ClientCertificateRequired = opts.RequireClientCert,
                EnabledSslProtocols = SslProtocols.Tls13,
                ApplicationProtocols = new List<SslApplicationProtocol> { new(NcpL2.Alpn) },
                RemoteCertificateValidationCallback = (_, cert, chain, _) =>
                {
                    if (!opts.RequireClientCert) return true;
                    if (cert is null)
                    {
                        log.LogWarning("L2: connection presented no client certificate.");
                        return false;
                    }
                    var leaf = cert as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(cert.GetRawCertData());
                    var intermediates = chain?.ChainElements.Skip(1).Select(e => e.Certificate).ToList()
                                        ?? new List<X509Certificate2>();
                    var res = NipMtlsValidator.ValidateClientCert(leaf, intermediates, _trustAnchors);
                    if (!res.Ok)
                    {
                        log.LogWarning("L2: client certificate rejected ({Code}): {Msg}", res.ErrorCode, res.Message);
                        return false;
                    }
                    boundNid = res.BoundNid;
                    return true;
                },
            };

            await ssl.AuthenticateAsServerAsync(authOpts, ct);
            // On some TLS 1.3/OpenSSL combinations the handshake can complete without invoking
            // the validation callback when the peer omits its certificate. Enforce the mTLS gate
            // again on the authenticated stream before any NCP byte can reach the backend.
            if (opts.RequireClientCert && ssl.RemoteCertificate is null)
            {
                log.LogWarning("L2: connection completed TLS without the required client certificate — closing.");
                return;
            }
            log.LogInformation(
                "L2: TLS handshake complete (alpn={Alpn}, bound_nid={Nid}).",
                ssl.NegotiatedApplicationProtocol.ToString(), boundNid ?? "<none>");

            // Mediate the native handshake until the post-Caps IdentFrame binds the mTLS NID.
            // This is deliberately full duplex: a conformant client waits for the backend's
            // CapsFrame before sending IdentFrame, so a read-ahead-only proxy would deadlock and
            // a raw proxy would let the interactive Ident bypass the certificate-NID check.
            await ProxyToBackendAsync(
                ssl,
                opts.RequireClientCert ? boundNid : null,
                ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "L2: connection error (bound_nid={Nid}).", boundNid ?? "<none>");
        }
        finally
        {
            await ssl.DisposeAsync();
        }
    }

    /// <summary>
    /// Reads the preamble plus HelloFrame before a backend connection is opened. Invalid
    /// pre-admission framing closes silently and never consumes backend capacity.
    /// </summary>
    private async Task<InitialHandshakeResult> ReadInitialHandshakeAsync(
        SslStream client,
        CancellationToken ct)
    {
        var preamble = new byte[NcpPreamble.Length];
        if (!await ReadExactInto(client, preamble, ct))
            return InitialHandshakeResult.ClientClosed;
        if (!NcpPreamble.TryValidate(preamble, out var preambleReason))
        {
            log.LogWarning("L2: {Code} — {Reason}; closing silently before backend admission.",
                NcpErrorCodes.PreambleInvalid, preambleReason);
            return InitialHandshakeResult.SilentReject;
        }

        var hello = await IngressFraming.ReadFrameAsync(client, opts.MaxHandshakeFrameBytes, ct);
        if (hello is null)
            return InitialHandshakeResult.ClientClosed;
        if (FrameHeader.Parse(hello).FrameType != FrameType.Hello)
        {
            log.LogWarning("L2: first post-preamble frame is not HelloFrame; closing silently before backend admission.");
            return InitialHandshakeResult.SilentReject;
        }

        return InitialHandshakeResult.Accepted([.. preamble, .. hello]);
    }

    /// <summary>
    /// Relays post-Hello client frames one at a time until the post-Caps IdentFrame has been
    /// checked against the mTLS-bound NID. The IdentFrame is never forwarded on a mismatch or
    /// unverifiable payload.
    /// </summary>
    private async Task<IngressAdmissionResult> ForwardUntilIdentityBoundAsync(
        SslStream client,
        Stream backend,
        string boundNid,
        CancellationToken ct)
    {
        while (true)
        {
            var frame = await IngressFraming.ReadFrameAsync(client, opts.MaxHandshakeFrameBytes, ct);
            if (frame is null)
                return IngressAdmissionResult.ClientClosed;

            switch (IngressFraming.ClassifyIdent(frame, out var identNid))
            {
                case IngressFraming.IdentScan.NotIdent:
                    // Preserve transparent proxy ordering while continuing to watch for the
                    // mandatory per-connection IdentFrame. The backend remains authoritative for
                    // whether a particular pre-Ident frame is legal at the NCP/NWP layer.
                    await backend.WriteAsync(frame, ct);
                    await backend.FlushAsync(ct);
                    continue;

                case IngressFraming.IdentScan.Unverifiable:
                    log.LogWarning("L2: {Code} — IdentFrame present but NID unverifiable (non-conformant tier/payload).",
                        NcpL2.NidMismatchCode);
                    return IngressAdmissionResult.RejectNid(
                        "IdentFrame NID is missing or cannot be verified.");

                case IngressFraming.IdentScan.Nid:
                    var bind = NipMtlsValidator.CheckSessionNidBinding(boundNid, identNid!);
                    if (!bind.Ok)
                    {
                        log.LogWarning("L2: {Code} — {Msg}", bind.ErrorCode, bind.Message);
                        return IngressAdmissionResult.RejectNid(
                            bind.Message ?? "Session NID mismatch.");
                    }
                    await backend.WriteAsync(frame, ct);
                    await backend.FlushAsync(ct);
                    log.LogInformation("L2: session NID binding ok (nid={Nid}).", boundNid);
                    return IngressAdmissionResult.Admitted;
            }
        }
    }

    private static async Task RejectNidMismatchAsync(SslStream ssl, string message, CancellationToken ct)
    {
        var wire = HandshakeCodec.Encode(new ErrorFrame
        {
            Status = NpsStatusCodes.AuthUnauthenticated,
            Error = NcpL2.NidMismatchCode,
            Message = message,
        }, EncodingTier.Json);
        await ssl.WriteAsync(wire, ct);
        await ssl.FlushAsync(ct);
    }

    private static async Task<bool> ReadExactInto(Stream s, byte[] buf, CancellationToken ct)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(read), ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    private async Task ProxyToBackendAsync(SslStream client, string? boundNid, CancellationToken ct)
    {
        if (boundNid is null)
        {
            using var rawBackend = new TcpClient();
            await rawBackend.ConnectAsync(opts.BackendHost, opts.BackendPort, ct);
            await ProxyRawAsync(client, rawBackend, rawBackend.GetStream(), ct);
            return;
        }

        using var admissionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        admissionCts.CancelAfter(TimeSpan.FromMilliseconds(opts.HandshakeTimeoutMs));

        InitialHandshakeResult initial;
        try
        {
            initial = await ReadInitialHandshakeAsync(client, admissionCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("L2: native handshake admission exceeded {TimeoutMs} ms — closing.",
                opts.HandshakeTimeoutMs);
            return;
        }
        if (initial.Outcome != IngressAdmissionOutcome.Admitted)
            return;

        using var backend = new TcpClient();
        try
        {
            await backend.ConnectAsync(opts.BackendHost, opts.BackendPort, admissionCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("L2: backend connection exceeded the {TimeoutMs} ms admission window — closing.",
                opts.HandshakeTimeoutMs);
            return;
        }
        var bs = backend.GetStream();
        try
        {
            await bs.WriteAsync(initial.Wire!, admissionCts.Token);
            await bs.FlushAsync(admissionCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("L2: initial handshake forwarding exceeded the {TimeoutMs} ms admission window — closing.",
                opts.HandshakeTimeoutMs);
            return;
        }
        var admission = ForwardUntilIdentityBoundAsync(client, bs, boundNid, admissionCts.Token);
        var backendToClient = bs.CopyToAsync(client, admissionCts.Token);

        var first = await Task.WhenAny(admission, backendToClient);
        if (first == backendToClient)
        {
            admissionCts.Cancel();
            await ObserveCompletionAsync(backendToClient);
            await ObserveCompletionAsync(admission);
            return;
        }

        IngressAdmissionResult result;
        try
        {
            result = await admission;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("L2: native handshake admission exceeded {TimeoutMs} ms — closing.",
                opts.HandshakeTimeoutMs);
            backend.Close();
            await ObserveCompletionAsync(backendToClient);
            return;
        }
        admissionCts.CancelAfter(Timeout.InfiniteTimeSpan);

        if (result.Outcome != IngressAdmissionOutcome.Admitted)
        {
            backend.Close();
            await ObserveCompletionAsync(backendToClient);
            if (result.Outcome == IngressAdmissionOutcome.RejectNid)
                await RejectNidMismatchAsync(client, result.Message!, ct);
            return;
        }

        var clientToBackend = client.CopyToAsync(bs, ct); // request bytes

        // Do NOT tear down as soon as either side finishes (Task.WhenAny truncates the in-flight
        // direction). When the client half-closes, signal the backend and drain its response in
        // full; when the backend closes first, its response is already fully relayed.
        await BidirectionalProxyCompletion.AwaitAsync(
            clientToBackend,
            backendToClient,
            () =>
            {
                try { backend.Client.Shutdown(SocketShutdown.Send); }
                catch { /* backend already gone */ }
            });
    }

    private static async Task ProxyRawAsync(
        SslStream client,
        TcpClient backend,
        Stream backendStream,
        CancellationToken ct)
    {
        var clientToBackend = client.CopyToAsync(backendStream, ct);
        var backendToClient = backendStream.CopyToAsync(client, ct);
        await BidirectionalProxyCompletion.AwaitAsync(
            clientToBackend,
            backendToClient,
            () =>
            {
                try { backend.Client.Shutdown(SocketShutdown.Send); }
                catch { /* backend already gone */ }
            });
    }

    private static async Task ObserveCompletionAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private List<X509Certificate2> LoadTrustAnchors()
    {
        var list = new List<X509Certificate2>();
        if (opts.TrustAnchorsDir is null || !Directory.Exists(opts.TrustAnchorsDir))
            return list;
        foreach (var file in Directory.EnumerateFiles(opts.TrustAnchorsDir))
        {
            try { list.Add(X509CertificateLoader.LoadCertificateFromFile(file)); }
            catch (Exception ex) { log.LogWarning(ex, "L2: skipping unreadable trust anchor {File}.", file); }
        }
        return list;
    }
}

internal enum IngressAdmissionOutcome
{
    Admitted,
    ClientClosed,
    SilentReject,
    RejectNid,
}

internal sealed record IngressAdmissionResult(IngressAdmissionOutcome Outcome, string? Message = null)
{
    public static IngressAdmissionResult Admitted { get; } = new(IngressAdmissionOutcome.Admitted);
    public static IngressAdmissionResult ClientClosed { get; } = new(IngressAdmissionOutcome.ClientClosed);
    public static IngressAdmissionResult RejectNid(string message) =>
        new(IngressAdmissionOutcome.RejectNid, message);
}

internal sealed record InitialHandshakeResult(IngressAdmissionOutcome Outcome, byte[]? Wire = null)
{
    public static InitialHandshakeResult ClientClosed { get; } =
        new(IngressAdmissionOutcome.ClientClosed);
    public static InitialHandshakeResult SilentReject { get; } =
        new(IngressAdmissionOutcome.SilentReject);
    public static InitialHandshakeResult Accepted(byte[] wire) =>
        new(IngressAdmissionOutcome.Admitted, wire);
}

/// <summary>
/// Coordinates bidirectional proxy completion without truncating the response after a client
/// half-close. Kept separate from socket setup so the state transition has deterministic tests.
/// </summary>
internal static class BidirectionalProxyCompletion
{
    internal static async Task AwaitAsync(
        Task clientToBackend,
        Task backendToClient,
        Action signalBackendSendCompleted)
    {
        var finished = await Task.WhenAny(clientToBackend, backendToClient);
        if (finished == clientToBackend)
        {
            signalBackendSendCompleted();
            await SwallowAsync(backendToClient); // deliver the remaining response
        }
        await SwallowAsync(clientToBackend);
        await SwallowAsync(backendToClient);
    }

    /// <summary>Awaits a copy task, swallowing teardown-race exceptions from closed streams.</summary>
    private static async Task SwallowAsync(Task copy)
    {
        try { await copy; }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
}
