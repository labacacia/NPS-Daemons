// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0
//
// nps-ingress — NPS Daemon, Layer 2 (Internet ingress).
// See docs/daemons/architecture.md for the role this binary plays.
//
// The HTTP listener is the observability surface. The native listener terminates
// NCP-over-TLS and proxies the verified stream to npsd. Admission controls beyond
// the RFC-0006 TLS boundary remain explicitly unsupported (see /health).
//
// Naming note: this is the *process* called "nps-ingress", distinct from
// the spec-level role of cluster control plane which is now called
// **Anchor Node** in NWP (NPS-CR-0001). This process MAY host an
// Anchor Node middleware via NPS.NWP.Anchor; that wiring is not implemented.

using NPS.Daemon.Ingress;

if (args is ["--healthcheck"])
{
    var port = int.TryParse(Environment.GetEnvironmentVariable("NPSINGRESS_PORT"), out var configuredPort)
        ? configuredPort
        : 8080;
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try
    {
        using var response = await client.GetAsync($"http://127.0.0.1:{port}/health");
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (HttpRequestException)
    {
        return 1;
    }
    catch (TaskCanceledException)
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);

var ingressOptions = IngressOptions.FromEnvironment();
builder.Services.AddSingleton(ingressOptions);

// L2 native-mode TLS terminator (NPS-RFC-0006 §6): ALPN nps/1.0, mutual TLS with NIP
// certificates, NID-bound sessions, proxy to the local backend. Runs alongside the HTTP
// health listener; stays idle until a server certificate is configured.
builder.Services.AddHostedService<NcpTlsListener>();

builder.WebHost.ConfigureKestrel(options =>
{
    var port = ingressOptions.HealthPort;
    var host = Environment.GetEnvironmentVariable("NPSINGRESS_HOST") ?? "0.0.0.0";
    if (host == "0.0.0.0")
    {
        options.ListenAnyIP(port);
    }
    else
    {
        options.ListenLocalhost(port);
    }
});

var app = builder.Build();

app.MapGet("/health", () => Results.Json(IngressHealthSnapshot.Create(ingressOptions)));

app.Logger.LogInformation("nps-ingress v1.0.0-alpha.18 starting (L2 NCP-over-TLS terminator per NPS-RFC-0006 §6 — enable via NPSINGRESS_CERT_PATH; see docs/daemons/architecture.md)");
app.Run();
return 0;
