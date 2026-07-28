// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0
//
// nps-registry — NPS Daemon, Layer 2 (cross-machine NDP discovery).
// See docs/daemons/architecture.md for the role this binary plays.
//
// Current reference daemon: SQLite-backed NDP registry with real Announce / Resolve / Graph.
// L2 federation lands in a later alpha.

using System.Text.Json;
using NPS.Daemon.Registry;
using NPS.NDP.Frames;
using NPS.NDP.Registry;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    var port = int.TryParse(Environment.GetEnvironmentVariable("NPSREGISTRY_PORT"), out var p) ? p : 17436;
    var host = Environment.GetEnvironmentVariable("NPSREGISTRY_HOST") ?? "0.0.0.0";
    if (host == "0.0.0.0")
        options.ListenAnyIP(port);
    else
        options.ListenLocalhost(port);
});

// ── Registry ──────────────────────────────────────────────────────────────────

var sqlitePath = Environment.GetEnvironmentVariable("NPSREGISTRY_SQLITE_PATH");

SqliteNdpRegistry? sqliteRegistry = null;
INdpRegistry registry;

if (!string.IsNullOrEmpty(sqlitePath))
{
    sqliteRegistry = new SqliteNdpRegistry(sqlitePath);
    registry       = sqliteRegistry;
}
else
{
    // In-memory fallback — useful for ephemeral single-instance deployments
    sqliteRegistry = SqliteNdpRegistry.CreateInMemory();
    registry       = sqliteRegistry;
}

builder.Services.AddSingleton(registry);
builder.Services.AddSingleton(sqliteRegistry);

// ── App ───────────────────────────────────────────────────────────────────────

var app = builder.Build();

app.MapGet("/health", (INdpRegistry reg) =>
{
    var all = reg.GetAll();
    return Results.Json(new
    {
        status    = "ok",
        daemon    = "nps-registry",
        version   = "1.0.0-alpha.17",
        layer     = 2,
        role      = "cross-machine NDP discovery",
        phase     = 2,
        entries   = all.Count,
        storage   = string.IsNullOrEmpty(sqlitePath) ? "in-memory" : "sqlite",
        spec_ref  = "spec/NPS-4-NDP.md",
    });
});

// ── POST /v1/announce ────────────────────────────────────────────────────────

app.MapPost("/v1/announce", async (HttpRequest req, INdpRegistry reg) =>
{
    AnnounceFrame? frame;
    try
    {
        frame = await req.ReadFromJsonAsync<AnnounceFrame>();
    }
    catch (JsonException ex)
    {
        return Results.Json(new
        {
            error   = "NDP-ANNOUNCE-INVALID",
            status  = "NPS-CLIENT-BAD-FRAME",
            message = $"announce body is not valid JSON: {ex.Message}",
        }, statusCode: 400);
    }

    if (frame is null || string.IsNullOrEmpty(frame.Nid))
    {
        return Results.Json(new
        {
            error   = "NDP-ANNOUNCE-INVALID",
            status  = "NPS-CLIENT-BAD-FRAME",
            message = "announce body is missing or nid is empty.",
        }, statusCode: 400);
    }

    reg.Announce(frame);

    return frame.Ttl == 0
        ? Results.Json(new { ok = true, nid = frame.Nid, status = "evicted" })
        : Results.Json(new { ok = true, nid = frame.Nid, status = "registered" });
});

// ── GET /v1/resolve ───────────────────────────────────────────────────────────

app.MapGet("/v1/resolve", (string target, INdpRegistry reg) =>
{
    if (string.IsNullOrWhiteSpace(target))
    {
        return Results.Json(new
        {
            error   = "NDP-RESOLVE-INVALID-TARGET",
            status  = "NPS-CLIENT-BAD-FRAME",
            message = "query parameter 'target' is required.",
        }, statusCode: 400);
    }

    var result = reg.Resolve(target);
    if (result is null)
    {
        return Results.Json(new
        {
            error   = "NDP-RESOLVE-NOT-FOUND",
            status  = "NPS-SERVER-UNAVAILABLE",
            message = $"no live registration found for target '{target}'.",
        }, statusCode: 404);
    }

    return Results.Json(new ResolveFrame
    {
        Target   = target,
        Resolved = result,
    });
});

// ── GET /v1/graph ─────────────────────────────────────────────────────────────

app.MapGet("/v1/graph", (SqliteNdpRegistry reg) =>
{
    var all  = reg.GetAll();
    var seq  = reg.GetSeq();
    var nodes = all
        .Select(f => new NdpGraphNode
        {
            Nid       = f.Nid,
            NodeRoles = f.NodeType is null ? null : new[] { f.NodeType },
        })
        .ToList();

    return Results.Json(new GraphFrame
    {
        GraphId = $"nps-registry:{seq}",
        Nodes   = nodes,
        Edges   = Array.Empty<NdpGraphEdge>(),
        Ttl     = 60,
        Metadata = JsonSerializer.SerializeToElement(new
        {
            initial_sync = true,
            seq,
        }),
    });
});

app.Logger.LogInformation(
    "nps-registry v1.0.0-alpha.17 starting on port 17436 (SQLite registry; storage={Storage}; see docs/daemons/architecture.md)",
    string.IsNullOrEmpty(sqlitePath) ? "in-memory" : sqlitePath);

app.Run();
