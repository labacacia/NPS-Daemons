// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0
//
// nps-registry — NPS Daemon, Layer 2 (cross-machine NDP discovery).
// See docs/daemons/architecture.md for the role this binary plays.
//
// Current reference daemon: SQLite-backed NDP registry with real Announce / Resolve / Graph,
// NPS-CR-0009 multi-Anchor cluster resolution, and NDP §9 federated cluster-tuple ingest.

using NPS.Daemon.Registry;

var opts = RegistryOptions.FromEnvironment();

if (args is ["--healthcheck"])
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try
    {
        using var response = await client.GetAsync($"http://127.0.0.1:{opts.Port}/health");
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

var app  = RegistryHost.Build(args, opts);

app.Logger.LogInformation(
    "nps-registry {Version} starting on {Host}:{Port} (SQLite registry; storage={Storage}; profile={Profile}; see docs/daemons/architecture.md)",
    RegistryHost.Version, opts.Host, opts.Port,
    string.IsNullOrEmpty(opts.SqlitePath) ? "in-memory" : opts.SqlitePath, opts.Profile);

app.Run();
return 0;

// Test bridge: NPS.Tests hosts the daemon through RegistryHost.WireServices / WireRoutes
// against Microsoft.AspNetCore.TestHost.TestServer.
