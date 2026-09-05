// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NPS.Core;
using NPS.Core.Codecs;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.Core.Ncp;
using NPS.Daemon.Ingress;
using Xunit;

namespace NPS.Daemon.Ingress.Tests;

/// <summary>
/// Executable NPS-Node-L2 §3.2 reference family. These tests cross real TLS sockets and use a
/// disposable Ed25519 NIP client PKI; helper-only validation is intentionally insufficient.
/// </summary>
public sealed class IngressTlsConformanceTests(IngressTlsPkiFixture pki)
    : IClassFixture<IngressTlsPkiFixture>
{
    private static readonly NpsFrameCodec Codec = NpsFrameCodec.CreateDefault();

    [Fact, Trait("ConformanceId", "TC-N2-Tls-01")]
    public async Task TC_N2_Tls_01_negotiates_only_nps_alpn_over_tls13()
    {
        await using var host = await IngressTlsTestHost.StartAsync(pki);

        var accepted = await host.RunOpenSslClientAsync(
            input: [],
            alpn: NcpL2.Alpn,
            withClientCertificate: true,
            quiet: false);
        var transcript = Encoding.UTF8.GetString(accepted.Stdout) + accepted.Stderr;
        Assert.True(
            transcript.Contains("TLSv1.3", StringComparison.OrdinalIgnoreCase),
            transcript);
        Assert.True(
            transcript.Contains("ALPN protocol: nps/1.0", StringComparison.OrdinalIgnoreCase),
            transcript);

        await using var rejectedHost = await IngressTlsTestHost.StartAsync(pki);
        var rejected = await rejectedHost.RunOpenSslClientAsync(
            input: [],
            alpn: "unknown/1",
            withClientCertificate: true,
            quiet: false);
        Assert.NotEqual(0, rejected.ExitCode);
    }

    [Fact, Trait("ConformanceId", "TC-N2-Tls-02")]
    public async Task TC_N2_Tls_02_requires_a_client_certificate()
    {
        await using var host = await IngressTlsTestHost.StartAsync(pki);

        var rejected = await host.RunOpenSslClientAsync(
            input: HandshakePrefix(IngressTlsPkiFixture.ClientNid),
            alpn: NcpL2.Alpn,
            withClientCertificate: false,
            quiet: true);
        Assert.Empty(rejected.Stdout);
        Assert.False(host.Backend.Pending());
    }

    [Fact, Trait("ConformanceId", "TC-N2-Tls-03")]
    public async Task TC_N2_Tls_03_validates_nip_chain_binds_nid_and_replays_prefix()
    {
        await using var host = await IngressTlsTestHost.StartAsync(pki);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var backendAccept = host.Backend.AcceptTcpClientAsync(timeout.Token).AsTask();
        await using var client = host.StartInteractiveOpenSslClient();
        var initial = PreambleAndHello();
        var ident = IdentFrame(IngressTlsPkiFixture.ClientNid);
        await client.WriteAsync(initial, timeout.Token);

        using var backend = await backendAccept;
        var backendStream = backend.GetStream();
        var forwardedInitial = new byte[initial.Length];
        await backendStream.ReadExactlyAsync(forwardedInitial, timeout.Token);
        Assert.Equal(initial, forwardedInitial);

        var caps = Frame(FrameType.Caps, "{}");
        await backendStream.WriteAsync(caps, timeout.Token);
        await backendStream.FlushAsync(timeout.Token);
        Assert.Equal(caps, await client.ReadFrameAsync(timeout.Token));

        await client.WriteAsync(ident, timeout.Token);
        var forwardedIdent = new byte[ident.Length];
        await backendStream.ReadExactlyAsync(forwardedIdent, timeout.Token);
        Assert.Equal(ident, forwardedIdent);

        backend.Client.Shutdown(SocketShutdown.Send);
        Assert.Equal(0, (await client.CompleteAsync(timeout.Token)).ExitCode);
    }

    [Fact, Trait("ConformanceId", "TC-N2-Tls-04")]
    public async Task TC_N2_Tls_04_returns_nid_mismatch_and_never_forwards_ident()
    {
        await using var host = await IngressTlsTestHost.StartAsync(pki);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var backendAccept = host.Backend.AcceptTcpClientAsync(timeout.Token).AsTask();
        await using var client = host.StartInteractiveOpenSslClient();
        var initial = PreambleAndHello();
        await client.WriteAsync(initial, timeout.Token);

        using var backend = await backendAccept;
        var backendStream = backend.GetStream();
        var forwardedInitial = new byte[initial.Length];
        await backendStream.ReadExactlyAsync(forwardedInitial, timeout.Token);
        Assert.Equal(initial, forwardedInitial);

        var caps = Frame(FrameType.Caps, "{}");
        await backendStream.WriteAsync(caps, timeout.Token);
        await backendStream.FlushAsync(timeout.Token);
        Assert.Equal(caps, await client.ReadFrameAsync(timeout.Token));

        await client.WriteAsync(
            IdentFrame("urn:nps:agent:ca.test.example:impostor"),
            timeout.Token);

        var error = Assert.IsType<ErrorFrame>(Codec.Decode(await client.ReadFrameAsync(timeout.Token)));
        Assert.Equal(NpsStatusCodes.AuthUnauthenticated, error.Status);
        Assert.Equal(NcpL2.NidMismatchCode, error.Error);
        var notForwarded = new byte[1];
        Assert.Equal(0, await backendStream.ReadAsync(notForwarded, timeout.Token));
        await client.CompleteAsync(timeout.Token);
    }

    [Fact]
    public async Task Invalid_preamble_closes_silently_without_backend_admission()
    {
        await using var host = await IngressTlsTestHost.StartAsync(pki);

        var rejected = await host.RunOpenSslClientAsync(
            Encoding.ASCII.GetBytes("BOGUS!!!"),
            NcpL2.Alpn,
            withClientCertificate: true,
            quiet: true);

        Assert.Empty(rejected.Stdout);
        Assert.False(host.Backend.Pending());
    }

    [Fact]
    public async Task Non_hello_first_frame_closes_silently_without_backend_admission()
    {
        await using var host = await IngressTlsTestHost.StartAsync(pki);
        byte[] invalid =
        [
            .. Encoding.ASCII.GetBytes(NcpPreamble.Literal),
            .. Frame(FrameType.Query, "{}"),
        ];

        var rejected = await host.RunOpenSslClientAsync(
            invalid,
            NcpL2.Alpn,
            withClientCertificate: true,
            quiet: true);

        Assert.Empty(rejected.Stdout);
        Assert.False(host.Backend.Pending());
    }

    [Fact]
    public async Task Oversized_hello_closes_without_allocating_or_connecting_backend()
    {
        await using var host = await IngressTlsTestHost.StartAsync(pki);
        var oversizedHeader = new byte[FrameHeader.ExtendedSize];
        new FrameHeader(
            FrameType.Hello,
            FrameFlags.Ext | FrameFlags.Tier1Json,
            PayloadLength: 4_000_000_000).WriteTo(oversizedHeader);

        var rejected = await host.RunOpenSslClientAsync(
            [.. Encoding.ASCII.GetBytes(NcpPreamble.Literal), .. oversizedHeader],
            NcpL2.Alpn,
            withClientCertificate: true,
            quiet: true);

        Assert.Empty(rejected.Stdout);
        Assert.False(host.Backend.Pending());
    }

    [Fact]
    public async Task Slow_incomplete_preamble_is_bounded_by_admission_timeout()
    {
        await using var host = await IngressTlsTestHost.StartAsync(pki, handshakeTimeoutMs: 150);
        await using var client = host.StartInteractiveOpenSslClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await client.WriteAsync(Encoding.ASCII.GetBytes("NPS"), timeout.Token);
        var result = await client.WaitForExitAsync(timeout.Token);

        Assert.NotEqual(-1, result.ExitCode);
        Assert.False(host.Backend.Pending());
    }

    [Fact]
    public async Task Malformed_ident_fails_closed_and_is_not_forwarded()
    {
        await using var host = await IngressTlsTestHost.StartAsync(pki);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var backendAccept = host.Backend.AcceptTcpClientAsync(timeout.Token).AsTask();
        await using var client = host.StartInteractiveOpenSslClient();
        var initial = PreambleAndHello();
        await client.WriteAsync(initial, timeout.Token);

        using var backend = await backendAccept;
        var backendStream = backend.GetStream();
        var forwardedInitial = new byte[initial.Length];
        await backendStream.ReadExactlyAsync(forwardedInitial, timeout.Token);

        var caps = Frame(FrameType.Caps, "{}");
        await backendStream.WriteAsync(caps, timeout.Token);
        await backendStream.FlushAsync(timeout.Token);
        Assert.Equal(caps, await client.ReadFrameAsync(timeout.Token));

        await client.WriteAsync(Frame(FrameType.Ident, "{\"nid\":"), timeout.Token);
        var error = Assert.IsType<ErrorFrame>(Codec.Decode(await client.ReadFrameAsync(timeout.Token)));
        Assert.Equal(NcpL2.NidMismatchCode, error.Error);
        Assert.Equal(0, await backendStream.ReadAsync(new byte[1], timeout.Token));
        await client.CompleteAsync(timeout.Token);
    }

    [Fact]
    public async Task Backend_connection_failure_closes_without_hanging_the_tls_peer()
    {
        await using var host = await IngressTlsTestHost.StartAsync(pki);
        host.Backend.Stop();

        var result = await host.RunOpenSslClientAsync(
            HandshakePrefix(IngressTlsPkiFixture.ClientNid),
            NcpL2.Alpn,
            withClientCertificate: true,
            quiet: true);

        Assert.Empty(result.Stdout);
    }

    private static byte[] HandshakePrefix(string identNid)
    {
        return [.. PreambleAndHello(), .. IdentFrame(identNid)];
    }

    private static byte[] PreambleAndHello() =>
        [.. Encoding.ASCII.GetBytes(NcpPreamble.Literal), .. Frame(FrameType.Hello, "{}")];

    private static byte[] IdentFrame(string nid) =>
        Frame(FrameType.Ident, $"{{\"nid\":\"{nid}\"}}");

    private static byte[] Frame(FrameType type, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var header = new FrameHeader(type, FrameFlags.Tier1Json | FrameFlags.Final, (uint)payload.Length);
        var wire = new byte[header.HeaderSize + payload.Length];
        header.WriteTo(wire);
        payload.CopyTo(wire, header.HeaderSize);
        return wire;
    }
}

public sealed class IngressTlsPkiFixture : IDisposable
{
    public const string ClientNid = "urn:nps:agent:ca.test.example:client-01";
    private const string Password = "nps-ingress-test";

    public string DirectoryPath { get; } = Path.Combine(
        Path.GetTempPath(),
        $"nps-ingress-tls-{Guid.NewGuid():N}");

    public string ServerPfxPath => Path.Combine(DirectoryPath, "server.pfx");
    public string ServerCertificatePath => Path.Combine(DirectoryPath, "server.crt");
    public string ClientCertificatePath => Path.Combine(DirectoryPath, "client.crt");
    public string ClientKeyPath => Path.Combine(DirectoryPath, "client.key");
    public string TrustAnchorsPath => Path.Combine(DirectoryPath, "trust");
    public string ServerPfxPassword => Password;

    public IngressTlsPkiFixture()
    {
        Directory.CreateDirectory(DirectoryPath);
        Directory.CreateDirectory(TrustAnchorsPath);
        CreateServerPfx();
        CreateNipClientPki();
    }

    public void Dispose()
    {
        try { Directory.Delete(DirectoryPath, recursive: true); } catch { /* best effort */ }
    }

    private void CreateServerPfx()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature,
            true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") },
            true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllBytes(ServerPfxPath, certificate.Export(X509ContentType.Pfx, Password));
        File.WriteAllText(ServerCertificatePath, certificate.ExportCertificatePem());
    }

    private void CreateNipClientPki()
    {
        RunOpenSsl("genpkey", "-algorithm", "ED25519", "-out", "root.key");
        RunOpenSsl(
            "req", "-new", "-x509", "-key", "root.key", "-out", "root.crt", "-days", "1",
            "-subj", "/CN=urn:nps:org:ca.test.example",
            "-addext", "basicConstraints=critical,CA:TRUE,pathlen:1",
            "-addext", "keyUsage=critical,keyCertSign,cRLSign");
        RunOpenSsl("genpkey", "-algorithm", "ED25519", "-out", "client.key");
        RunOpenSsl(
            "req", "-new", "-key", "client.key", "-out", "client.csr",
            "-subj", $"/CN={ClientNid}");

        File.WriteAllText(
            Path.Combine(DirectoryPath, "client.ext"),
            $"basicConstraints=critical,CA:FALSE{Environment.NewLine}" +
            $"keyUsage=critical,digitalSignature{Environment.NewLine}" +
            $"extendedKeyUsage=critical,clientAuth,1.3.6.1.4.1.65715.1.1{Environment.NewLine}" +
            $"subjectAltName=URI:{ClientNid}{Environment.NewLine}");
        RunOpenSsl(
            "x509", "-req", "-in", "client.csr", "-CA", "root.crt", "-CAkey", "root.key",
            "-set_serial", "2", "-days", "1", "-out", "client.crt", "-extfile", "client.ext");
        File.Copy(Path.Combine(DirectoryPath, "root.crt"), Path.Combine(TrustAnchorsPath, "root.crt"));
    }

    private void RunOpenSsl(params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "openssl",
                WorkingDirectory = DirectoryPath,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            },
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"openssl {string.Join(' ', arguments)} failed ({process.ExitCode}): {stdout}{stderr}");
    }
}

internal sealed class IngressTlsTestHost : IAsyncDisposable
{
    private readonly NcpTlsListener _listener;
    private readonly IngressTlsPkiFixture _pki;

    public TcpListener Backend { get; }
    public int TlsPort { get; }

    private IngressTlsTestHost(
        NcpTlsListener listener,
        IngressTlsPkiFixture pki,
        TcpListener backend,
        int tlsPort)
    {
        _listener = listener;
        _pki = pki;
        Backend = backend;
        TlsPort = tlsPort;
    }

    public static async Task<IngressTlsTestHost> StartAsync(
        IngressTlsPkiFixture pki,
        int handshakeTimeoutMs = 10_000)
    {
        var backend = new TcpListener(IPAddress.Loopback, 0);
        backend.Start();
        var backendPort = ((IPEndPoint)backend.LocalEndpoint).Port;
        var tlsPort = ReservePort();
        var options = new IngressOptions
        {
            TlsPort = tlsPort,
            BackendHost = IPAddress.Loopback.ToString(),
            BackendPort = backendPort,
            ServerCertPath = pki.ServerPfxPath,
            ServerCertPassword = pki.ServerPfxPassword,
            TrustAnchorsDir = pki.TrustAnchorsPath,
            RequireClientCert = true,
            HandshakeTimeoutMs = handshakeTimeoutMs,
        };
        var listener = new NcpTlsListener(options, NullLogger<NcpTlsListener>.Instance);
        await listener.StartAsync(CancellationToken.None);
        await WaitForListenerAsync(tlsPort);
        return new IngressTlsTestHost(listener, pki, backend, tlsPort);
    }

    public async Task<OpenSslClientResult> RunOpenSslClientAsync(
        byte[] input,
        string alpn,
        bool withClientCertificate,
        bool quiet,
        bool waitForServerResponse = false)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "openssl",
                RedirectStandardError = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            },
        };
        var arguments = process.StartInfo.ArgumentList;
        arguments.Add("s_client");
        arguments.Add("-connect");
        arguments.Add($"127.0.0.1:{TlsPort}");
        arguments.Add("-servername");
        arguments.Add("localhost");
        arguments.Add("-tls1_3");
        arguments.Add("-alpn");
        arguments.Add(alpn);
        arguments.Add("-CAfile");
        arguments.Add(_pki.ServerCertificatePath);
        arguments.Add("-verify_return_error");
        if (quiet)
        {
            arguments.Add("-quiet");
            if (!waitForServerResponse)
                arguments.Add("-no_ign_eof");
        }
        else
            arguments.Add("-prexit");
        if (withClientCertificate)
        {
            arguments.Add("-cert");
            arguments.Add(_pki.ClientCertificatePath);
            arguments.Add("-key");
            arguments.Add(_pki.ClientKeyPath);
        }

        process.Start();
        var stdout = new MemoryStream();
        var stdoutCopy = process.StandardOutput.BaseStream.CopyToAsync(stdout);
        var stderrRead = process.StandardError.ReadToEndAsync();
        if (input.Length > 0)
            await process.StandardInput.BaseStream.WriteAsync(input);
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        await stdoutCopy;
        return new OpenSslClientResult(process.ExitCode, stdout.ToArray(), await stderrRead);
    }

    public OpenSslInteractiveClient StartInteractiveOpenSslClient() =>
        OpenSslInteractiveClient.Start(TlsPort, _pki);

    public async ValueTask DisposeAsync()
    {
        await _listener.StopAsync(CancellationToken.None);
        _listener.Dispose();
        Backend.Stop();
    }

    private static int ReservePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task WaitForListenerAsync(int port)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            using var probe = new TcpClient();
            try
            {
                await probe.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException ex)
            {
                lastError = ex;
                await Task.Delay(20);
            }
        }
        throw new TimeoutException($"nps-ingress test listener on {port} did not become ready.", lastError);
    }
}

internal sealed record OpenSslClientResult(int ExitCode, byte[] Stdout, string Stderr);

internal sealed class OpenSslInteractiveClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _stderr;
    private bool _completed;

    private OpenSslInteractiveClient(Process process)
    {
        _process = process;
        _stderr = process.StandardError.ReadToEndAsync();
    }

    public static OpenSslInteractiveClient Start(int tlsPort, IngressTlsPkiFixture pki)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "openssl",
                RedirectStandardError = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            },
        };
        var arguments = process.StartInfo.ArgumentList;
        arguments.Add("s_client");
        arguments.Add("-connect");
        arguments.Add($"127.0.0.1:{tlsPort}");
        arguments.Add("-servername");
        arguments.Add("localhost");
        arguments.Add("-tls1_3");
        arguments.Add("-alpn");
        arguments.Add(NcpL2.Alpn);
        arguments.Add("-CAfile");
        arguments.Add(pki.ServerCertificatePath);
        arguments.Add("-verify_return_error");
        arguments.Add("-quiet");
        arguments.Add("-no_ign_eof");
        arguments.Add("-cert");
        arguments.Add(pki.ClientCertificatePath);
        arguments.Add("-key");
        arguments.Add(pki.ClientKeyPath);
        process.Start();
        return new OpenSslInteractiveClient(process);
    }

    public async Task WriteAsync(byte[] wire, CancellationToken ct)
    {
        await _process.StandardInput.BaseStream.WriteAsync(wire, ct);
        await _process.StandardInput.BaseStream.FlushAsync(ct);
    }

    public async Task<byte[]> ReadFrameAsync(CancellationToken ct)
    {
        var stream = _process.StandardOutput.BaseStream;
        var first = new byte[2];
        await stream.ReadExactlyAsync(first, ct);
        var extended = ((FrameFlags)first[1] & FrameFlags.Ext) != 0;
        var headerSize = extended ? FrameHeader.ExtendedSize : FrameHeader.DefaultSize;
        var headerWire = new byte[headerSize];
        first.CopyTo(headerWire, 0);
        await stream.ReadExactlyAsync(headerWire.AsMemory(2), ct);
        var header = FrameHeader.Parse(headerWire);
        var frame = new byte[headerSize + checked((int)header.PayloadLength)];
        headerWire.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(headerSize), ct);
        return frame;
    }

    public async Task<OpenSslClientResult> CompleteAsync(CancellationToken ct)
    {
        if (!_completed)
        {
            _process.StandardInput.Close();
            await _process.WaitForExitAsync(ct);
            _completed = true;
        }
        return new OpenSslClientResult(_process.ExitCode, [], await _stderr);
    }

    public async Task<OpenSslClientResult> WaitForExitAsync(CancellationToken ct)
    {
        if (!_completed)
        {
            await _process.WaitForExitAsync(ct);
            _completed = true;
        }
        return new OpenSslClientResult(_process.ExitCode, [], await _stderr);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed && !_process.HasExited)
        {
            _process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await _process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { _process.Kill(entireProcessTree: true); }
        }
        _process.Dispose();
    }
}
