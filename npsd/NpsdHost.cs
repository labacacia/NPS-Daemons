// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.Daemon.Npsd.Endpoints;
using NPS.Daemon.Npsd.Inbox;
using NPS.Daemon.Npsd.Observability;
using NPS.Daemon.Npsd.SubNids;
using NPS.Daemon.Observability;
using NPS.Daemon.Observability.HealthChecks;
using NPS.Daemon.Observability.Logging;
using NPS.Daemon.Observability.Metrics;
using NPS.Daemon.Observability.Shutdown;

namespace NPS.Daemon.Npsd;

/// <summary>
/// Builds the <c>npsd</c> ASP.NET Core <see cref="WebApplication"/>.
/// </summary>
/// <remarks>
/// <para>
/// Production entrypoint <c>Program.cs</c> calls <see cref="Build"/> with
/// <see cref="NpsdOptions.FromEnvironment"/> and then <c>app.Run()</c>.
/// </para>
/// <para>
/// Tests can call <see cref="BuildForTests"/> to get a fully wired
/// <see cref="WebApplication"/> with an in-memory SQLite store and a
/// fresh root keypair under a temp directory, then host it with
/// <c>Microsoft.AspNetCore.TestHost.TestServer</c>.
/// </para>
/// </remarks>
public static class NpsdHost
{
    /// <summary>Default factory for the production binary.</summary>
    public static WebApplication Build(string[] args, NpsdOptions? opts = null)
    {
        opts ??= NpsdOptions.FromEnvironment();

        var builder = WebApplication.CreateBuilder(args);

        builder.Logging.AddNpsJsonConsole();

        builder.WebHost.ConfigureKestrel(options =>
        {
            if (opts.Host == "0.0.0.0")
                options.ListenAnyIP(opts.Port);
            else
                options.ListenLocalhost(opts.Port);
        });

        WireServices(builder.Services, opts, useInMemorySqlite: false);

        var app = builder.Build();
        WireRoutes(app, opts);
        return app;
    }

    /// <summary>
    /// Wires npsd's services into a caller-supplied <see cref="IServiceCollection"/>
    /// and routes onto a caller-supplied <see cref="WebApplication"/>. Exposed
    /// so test fixtures can swap the host's transport (e.g.
    /// <c>UseTestServer</c>) without dragging the
    /// <c>Microsoft.AspNetCore.TestHost</c> package into the npsd binary.
    /// </summary>
    public static void WireServices(IServiceCollection services, NpsdOptions opts, bool useInMemorySqlite = false)
    {
        services.AddSingleton(opts);
        services.AddSingleton(_ => RootIdentity.LoadOrCreate(opts.DataDir));
        services.AddSingleton(sp =>
        {
            if (useInMemorySqlite) return SubNidStore.CreateInMemoryForTests();
            Directory.CreateDirectory(opts.DataDir);
            return new SubNidStore(Path.Combine(opts.DataDir, "sub-nids.sqlite"));
        });
        services.AddSingleton<SubNidService>();
        services.AddSingleton<InboxStore>();

        // ── Observability baseline (NPS-Dev #45) ─────────────────────────────
        services.AddNpsObservability();
        services.AddSingleton<NpsdMetrics>();
        services.AddReadinessProbe("storage", (sp, _) =>
        {
            var store = sp.GetRequiredService<SubNidStore>();
            try { store.Ping(); return Task.FromResult<string?>(null); }
            catch (Exception ex) { return Task.FromResult<string?>($"sqlite unavailable: {ex.Message}"); }
        });
        services.AddReadinessProbe("key_material", (sp, _) =>
        {
            var root = sp.GetService<RootIdentity>();
            return Task.FromResult<string?>(
                root is null ? "root identity not loaded" : null);
        });
    }

    /// <summary>
    /// Wires npsd's HTTP routes onto a built <see cref="WebApplication"/>.
    /// Pair with <see cref="WireServices"/>.
    /// </summary>
    public static void WireRoutes(WebApplication app, NpsdOptions opts)
    {
        var rootKey = app.Services.GetRequiredService<RootIdentity>();
        app.Logger.LogInformation(
            "npsd starting; root NID host fingerprint = {Fingerprint}; bind = {Host}:{Port}",
            rootKey.HostFingerprint, opts.Host, opts.Port);

        // Hook SIGTERM → flip liveness gate so /healthz starts failing.
        app.UseShutdownLivenessGate();

        // Per-request connection + frame counters (must run before route mapping
        // so it observes every request, including health probes).
        app.UseMiddleware<NpsdMetricsMiddleware>();

        // ── /healthz, /readyz, /metrics ──────────────────────────────────────
        app.MapNpsObservability();

        // ── /health (legacy npsd-shaped JSON; kept for compatibility) ────────
        app.MapGet("/health", (RootIdentity key, SubNidService nids, InboxStore inbox, ShutdownState shutdown) =>
        {
            if (shutdown.IsStopping)
                return Results.Json(new
                {
                    status = "stopping",
                    daemon = "npsd",
                }, s_jsonOpts, statusCode: 503);
            return Results.Json(new
            {
                status         = "ok",
                daemon         = "npsd",
                version        = "1.0.0-alpha.16",
                layer          = 1,
                role           = "host-local NCP wire + state host",
                port           = opts.Port,
                host_nid       = nids.HostNid,
                host_nid_fpr   = key.HostFingerprint,
                spec_reference = "docs/daemons/architecture.md",
                sub_nids = new
                {
                    count = nids.List(limit: 1, offset: 0) is var probe && probe.Count > 0
                        ? "≥1" : "0",
                },
            }, s_jsonOpts);
        });

        // ── /.nwm — minimal Neural Web Manifest for the daemon itself ─────────
        app.MapGet("/.nwm", (RootIdentity key, SubNidService nids) => Results.Content(
            JsonSerializer.Serialize(new
            {
                nwp                 = "0.7",
                node_id             = $"{nids.HostNid}:npsd",
                node_type           = "memory",
                display_name        = "NPS Daemon (npsd)",
                wire_formats        = new[] { "json", "msgpack" },
                preferred_format    = "json",
                capabilities        = new
                {
                    query     = true,
                    subscribe = false,
                    sub_nids  = true,
                    inbox     = true,
                },
                auth                = new { required = false },
                endpoints           = new
                {
                    manifest             = "/.nwm",
                    health               = "/health",
                    healthz              = "/healthz",
                    readyz               = "/readyz",
                    metrics              = "/metrics",
                    sub_nid_issue        = "/v1/agents",
                    sub_nid_list         = "/v1/agents",
                    sub_nid_get          = "/v1/agents/{nid}",
                    sub_nid_revoke       = "/v1/agents/{nid}/revoke",
                    inbox_deposit        = "/v1/inbox/{nid}",
                    inbox_long_poll      = "/v1/inbox/{nid}",
                    inbox_ack            = "/v1/inbox/{nid}/{message_id}",
                    inbox_depth          = "/v1/inbox/{nid}/depth",
                },
                min_assurance_level = "anonymous",
            }, s_jsonOpts),
            contentType: "application/nwp-manifest+json"));

        app.MapSubNidEndpoints();
        app.MapInboxEndpoints();
    }

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented          = false,
    };
}
