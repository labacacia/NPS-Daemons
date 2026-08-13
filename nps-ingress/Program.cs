// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0
//
// nps-ingress — NPS Daemon, Layer 2 (Internet ingress).
// See docs/daemons/architecture.md for the role this binary plays.
//
// Phase 1 (v1.0-alpha.3): public-facing HTTP listener with /health
// only. TLS termination, rate limit, NeuronHub auth, CGN debit, and
// reputation policy lookup all land in alpha.4 onwards.
//
// Naming note: this is the *process* called "nps-ingress", distinct from
// the spec-level role of cluster control plane which is now called
// **Anchor Node** in NWP (NPS-CR-0001). This process MAY host an
// Anchor Node middleware via NPS.NWP.Anchor; that wiring lands in
// alpha.4.

using NPS.Daemon.Ingress;

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

app.MapGet("/health", () => Results.Json(new
{
    status         = "ok",
    daemon         = "nps-ingress",
    version        = "1.0.0-alpha.18",
    layer          = 2,
    role           = "Internet ingress (L2: NCP-over-TLS terminator)",
    phase          = 2,
    spec_reference = "NPS-RFC-0006 §6 (native-mode TLS binding); docs/daemons/architecture.md",
    done           = new[]
    {
        "L2 native-mode TLS terminator: ALPN nps/1.0, mutual TLS with NIP-cert validation + session-NID binding (NCP-NID-MISMATCH), proxy to backend (alpha.13, NPS-RFC-0006 §6)",
    },
    todo           = new[]
    {
        "inline IdentFrame-NID cross-check on the terminated stream",
        "TC-N2-* L2 conformance",
        "rate limiting / CGN debit trigger",
        "NPS-RFC-0004 reputation policy lookup",
        "NPS.NWP.Anchor middleware wiring",
    },
}));

app.Logger.LogInformation("nps-ingress v1.0.0-alpha.18 starting (L2 NCP-over-TLS terminator per NPS-RFC-0006 §6 — enable via NPSINGRESS_CERT_PATH; see docs/daemons/architecture.md)");
app.Run();
