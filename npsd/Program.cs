// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0
//
// npsd — NPS Daemon, Layer 1 (host-local NCP wire + state host).
// See docs/daemons/architecture.md for the role this binary plays in
// the broader NPS deployment topology.

using NPS.Daemon.Npsd;

if (args is ["--healthcheck"])
{
    var port = int.TryParse(Environment.GetEnvironmentVariable("NPSD_PORT"), out var configuredPort)
        ? configuredPort
        : 17433;
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try
    {
        using var response = await client.GetAsync($"http://127.0.0.1:{port}/healthz");
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

NpsdHost.Build(args).Run();
return 0;

// Test bridge: NPS.Tests references npsd's classes via InternalsVisibleTo
// (see Npsd.csproj). The factory for tests is `NpsdHost.BuildForTests`.
