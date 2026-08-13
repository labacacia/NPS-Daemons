// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using NPS.NDP;
using NPS.NDP.Frames;
using NPS.NDP.Registry;

namespace NPS.Daemon.Registry;

/// <summary>
/// Builds the <c>nps-registry</c> ASP.NET Core <see cref="WebApplication"/>.
///
/// <para>The production entrypoint <c>Program.cs</c> calls <see cref="Build"/> with
/// <see cref="RegistryOptions.FromEnvironment"/> and then <c>app.Run()</c>. Tests call
/// <see cref="WireServices"/> / <see cref="WireRoutes"/> against a
/// <c>Microsoft.AspNetCore.TestHost.TestServer</c>-backed builder.</para>
/// </summary>
public static class RegistryHost
{
    /// <summary>Daemon version banner, kept in step with <c>NpsRegistry.csproj</c>.</summary>
    public const string Version = "1.0.0-alpha.16";

    /// <summary>Default factory for the production binary.</summary>
    public static WebApplication Build(string[] args, RegistryOptions? opts = null)
    {
        opts ??= RegistryOptions.FromEnvironment();

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(options =>
        {
            if (opts.Host == "0.0.0.0")
                options.ListenAnyIP(opts.Port);
            else
                options.ListenLocalhost(opts.Port);
        });

        WireServices(builder.Services, opts);
        var app = builder.Build();
        WireRoutes(app, opts);
        return app;
    }

    /// <summary>
    /// Registers the registry backend. A file-backed store is used when
    /// <see cref="RegistryOptions.SqlitePath"/> is set, otherwise an ephemeral in-memory one.
    /// </summary>
    public static void WireServices(IServiceCollection services, RegistryOptions opts)
    {
        var registry = string.IsNullOrEmpty(opts.SqlitePath)
            ? SqliteNdpRegistry.CreateInMemory()
            : new SqliteNdpRegistry(opts.SqlitePath);

        services.AddSingleton(opts);
        services.AddSingleton(registry);
        services.AddSingleton<INdpRegistry>(registry);
    }

    /// <summary>Maps every HTTP endpoint the daemon serves.</summary>
    public static void WireRoutes(WebApplication app, RegistryOptions opts)
    {
        MapHealth(app, opts);
        MapAnnounce(app, opts);
        MapResolve(app);
        MapClusterResolve(app);
        MapFederation(app, opts);
        MapGraph(app);
    }

    // ── GET /health ───────────────────────────────────────────────────────────

    private static void MapHealth(WebApplication app, RegistryOptions opts) =>
        app.MapGet("/health", (SqliteNdpRegistry reg) => Results.Json(new
        {
            status   = "ok",
            daemon   = "nps-registry",
            version  = Version,
            layer    = 2,
            role     = "cross-machine NDP discovery",
            phase    = 2,
            entries  = reg.GetAll().Count,
            clusters = reg.GetAllClusterOwnership().Count,
            storage  = string.IsNullOrEmpty(opts.SqlitePath) ? "in-memory" : "sqlite",
            profile  = opts.Profile,
            nid      = opts.Nid,
            spec_ref = "spec/NPS-4-NDP.md",
        }));

    // ── POST /v1/announce ─────────────────────────────────────────────────────

    private static void MapAnnounce(WebApplication app, RegistryOptions opts) =>
        app.MapPost("/v1/announce", async (HttpRequest req, SqliteNdpRegistry reg, ILoggerFactory logs) =>
        {
            var log = logs.CreateLogger("nps-registry.announce");

            // NDP §9 federation forwarding: loop detection and the 3-hop ceiling are
            // evaluated before the frame touches registry state.
            var forwarded = Disposition(req, opts, log);
            if (forwarded.Loop)      return FederationLoop(opts.Nid);
            if (forwarded.HopsMaxed) return Results.Json(new { ok = true, status = "dropped", reason = "ndp-federation-max-hops" });

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

            if (frame.Ttl == 0)
                return Results.Json(new { ok = true, nid = frame.Nid, status = "evicted" });

            // NPS-CR-0009: echo the fence the registry stored, so a publisher can see that a
            // stale (lower) cluster_epoch was not accepted as a downgrade.
            var stored = reg.GetByNid(frame.Nid);
            return Results.Json(new
            {
                ok             = true,
                nid            = frame.Nid,
                status         = "registered",
                cluster_anchor = stored?.ClusterAnchor,
                cluster_epoch  = stored?.ClusterEpoch,
                forwarded_by   = forwarded.NextHeader,
            });
        });

    // ── GET /v1/resolve ───────────────────────────────────────────────────────

    private static void MapResolve(WebApplication app) =>
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
                    error   = NdpErrorCodes.ResolveNotFound,
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

    // ── GET /v1/cluster/resolve ───────────────────────────────────────────────
    //
    // NPS-CR-0009 §3.4 / NDP §9: resolve a cluster_anchor NID to its current active Anchor.

    private static void MapClusterResolve(WebApplication app) =>
        app.MapGet("/v1/cluster/resolve", (string? cluster_anchor, SqliteNdpRegistry reg) =>
        {
            if (string.IsNullOrWhiteSpace(cluster_anchor))
            {
                return Results.Json(new
                {
                    error   = "NDP-RESOLVE-INVALID-TARGET",
                    status  = "NPS-CLIENT-BAD-FRAME",
                    message = "query parameter 'cluster_anchor' is required.",
                }, statusCode: 400);
            }

            AnnounceFrame? active;
            try
            {
                active = reg.ResolveCluster(cluster_anchor);
            }
            catch (NdpClusterSplitException ex)
            {
                return ClusterSplit(ex.ClusterAnchor, ex.Epoch, ex.Message);
            }

            var localEpoch = active?.ClusterEpoch ?? SqliteNdpRegistry.DefaultClusterEpoch;
            var owner      = reg.GetClusterOwnership(cluster_anchor);

            // A federated peer may know a higher epoch than any Anchor announced locally
            // (NDP §9 "MUST prefer a higher cluster_epoch received from a peer").
            if (owner is not null && (active is null || owner.ClusterEpoch > localEpoch))
            {
                var member = reg.GetByNid(owner.ActiveNid);
                return Results.Json(new ClusterResolveResponse
                {
                    ClusterAnchor = cluster_anchor,
                    ClusterEpoch  = owner.ClusterEpoch,
                    ActiveNid     = owner.ActiveNid,
                    Source        = owner.Source,
                    Resolved      = FirstEndpoint(member),
                });
            }

            if (active is null)
            {
                return Results.Json(new
                {
                    error   = NdpErrorCodes.ResolveNotFound,
                    status  = "NPS-CLIENT-NOT-FOUND",
                    message = $"no live Anchor for cluster '{cluster_anchor}'.",
                }, statusCode: 404);
            }

            return Results.Json(new ClusterResolveResponse
            {
                ClusterAnchor = cluster_anchor,
                ClusterEpoch  = localEpoch,
                ActiveNid     = active.Nid,
                Source        = SqliteNdpRegistry.LocalSource,
                Resolved      = FirstEndpoint(active),
            });
        });

    // ── Federation (NDP §9 / §7.6) ────────────────────────────────────────────

    private static void MapFederation(WebApplication app, RegistryOptions opts)
    {
        // The tuples this registry would propagate to its peers.
        app.MapGet("/v1/federation/clusters", (SqliteNdpRegistry reg) => Results.Json(new
        {
            registry_nid = opts.Nid,
            clusters     = reg.GetAllClusterOwnership().Select(o => new ClusterTupleBody
            {
                ClusterAnchor = o.ClusterAnchor,
                ClusterEpoch  = o.ClusterEpoch,
                ActiveNid     = o.ActiveNid,
            }),
        }));

        // Ingest a (cluster_anchor, cluster_epoch, active_nid) tuple from a peer registry.
        app.MapPost("/v1/federation/cluster",
            async (HttpRequest req, SqliteNdpRegistry reg, ILoggerFactory logs) =>
        {
            var log = logs.CreateLogger("nps-registry.federation");

            if (!opts.FederationEnabled)
            {
                return Results.Json(new
                {
                    error   = NdpErrorCodes.AnnounceProfileViolation,
                    status  = "NPS-AUTH-FORBIDDEN",
                    message = $"federation requires the '{RegistryOptions.ProfilePublicFederated}' "
                            + $"registry profile; this registry runs '{opts.Profile}' (NDP §7.3).",
                }, statusCode: 403);
            }

            var forwarded = Disposition(req, opts, log);
            if (forwarded.Loop)      return FederationLoop(opts.Nid);
            if (forwarded.HopsMaxed) return Results.Json(new { ok = true, status = "dropped", reason = "ndp-federation-max-hops" });

            ClusterTupleBody? body;
            try
            {
                body = await req.ReadFromJsonAsync<ClusterTupleBody>();
            }
            catch (JsonException ex)
            {
                return Results.Json(new
                {
                    error   = "NDP-ANNOUNCE-INVALID",
                    status  = "NPS-CLIENT-BAD-FRAME",
                    message = $"cluster tuple is not valid JSON: {ex.Message}",
                }, statusCode: 400);
            }

            if (body is null || string.IsNullOrEmpty(body.ClusterAnchor) || string.IsNullOrEmpty(body.ActiveNid))
            {
                return Results.Json(new
                {
                    error   = "NDP-ANNOUNCE-INVALID",
                    status  = "NPS-CLIENT-BAD-FRAME",
                    message = "cluster tuple requires 'cluster_anchor' and 'active_nid'.",
                }, statusCode: 400);
            }

            var epoch  = body.ClusterEpoch ?? SqliteNdpRegistry.DefaultClusterEpoch;
            var source = forwarded.Hops.Count > 0 ? forwarded.Hops[^1] : "peer";
            var result = reg.ApplyClusterOwnership(body.ClusterAnchor, epoch, body.ActiveNid, source);

            if (result == ClusterOwnershipOutcome.Split)
            {
                var owner = reg.GetClusterOwnership(body.ClusterAnchor);
                return ClusterSplit(body.ClusterAnchor, epoch,
                    $"peer advertises '{body.ActiveNid}' as active for cluster '{body.ClusterAnchor}' at "
                    + $"epoch {epoch}, but '{owner?.ActiveNid}' is already recorded active at that epoch.");
            }

            var current = reg.GetClusterOwnership(body.ClusterAnchor);
            return Results.Json(new
            {
                ok             = true,
                status         = result == ClusterOwnershipOutcome.Applied ? "applied" : "ignored",
                cluster_anchor = body.ClusterAnchor,
                cluster_epoch  = current?.ClusterEpoch ?? epoch,
                active_nid     = current?.ActiveNid ?? body.ActiveNid,
                source         = current?.Source,
                forwarded_by   = forwarded.NextHeader,
            });
        });
    }

    // ── GET /v1/graph ─────────────────────────────────────────────────────────

    private static void MapGraph(WebApplication app) =>
        app.MapGet("/v1/graph", (SqliteNdpRegistry reg) =>
        {
            var all = reg.GetAll();
            var seq = reg.GetSeq();

            return Results.Json(new GraphFrame
            {
                GraphId = $"nps-registry:{seq}",
                Nodes   = all.Select(f => new NdpGraphNode
                {
                    Nid           = f.Nid,
                    ClusterAnchor = f.ClusterAnchor,
                    NodeRoles     = f.NodeType is null ? null : new[] { f.NodeType },
                }).ToList(),
                Edges   = Array.Empty<NdpGraphEdge>(),
                Ttl     = 60,
                Metadata = JsonSerializer.SerializeToElement(new
                {
                    initial_sync = true,
                    seq,
                }),
            });
        });

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Evaluates the NDP §9 <c>ndp-forwarded-by</c> hop list for an inbound request.</summary>
    private static ForwardedDisposition Disposition(HttpRequest req, RegistryOptions opts, ILogger log)
    {
        var header = req.Headers[NdpFederation.ForwardedByHeader].ToString();
        if (string.IsNullOrWhiteSpace(header))
            return new ForwardedDisposition(Array.Empty<string>(), NextHeader: null, Loop: false, HopsMaxed: false);

        var hops = NdpFederation.ParseForwardedBy(header);

        if (!opts.FederationEnabled)
        {
            // NDP §9: local-dev / org-private registries do not forward; they MAY log a warning
            // when a forwarded frame arrives. The frame is still ingested locally.
            log.LogWarning(
                "forwarded frame arrived on a '{Profile}' registry (ndp-forwarded-by: {Hops}); NDP §9 permits no onward forwarding",
                opts.Profile, header);
        }

        try
        {
            var next = NdpFederation.AppendForwardedBy(opts.Nid, header);
            return next is null
                ? new ForwardedDisposition(hops, null, Loop: false, HopsMaxed: true)
                : new ForwardedDisposition(hops, next, Loop: false, HopsMaxed: false);
        }
        catch (ArgumentException)
        {
            return new ForwardedDisposition(hops, null, Loop: true, HopsMaxed: false);
        }
    }

    private static IResult FederationLoop(string ownNid) => Results.Json(new
    {
        error   = NdpErrorCodes.FederationLoop,
        status  = "NPS-CLIENT-CONFLICT",
        message = $"'{ownNid}' already appears in {NdpFederation.ForwardedByHeader}.",
    }, statusCode: 409);

    private static IResult ClusterSplit(string clusterAnchor, ulong epoch, string message) => Results.Json(new
    {
        error          = NdpErrorCodes.ClusterSplit,
        status         = "NPS-CLIENT-CONFLICT",
        cluster_anchor = clusterAnchor,
        cluster_epoch  = epoch,
        message,
    }, statusCode: 409);

    private static NdpResolveResult? FirstEndpoint(AnnounceFrame? frame)
    {
        var addr = frame?.Addresses.FirstOrDefault();
        return addr is null ? null : new NdpResolveResult { Host = addr.Host, Port = addr.Port, Ttl = frame!.Ttl };
    }

    private readonly record struct ForwardedDisposition(
        IReadOnlyList<string> Hops, string? NextHeader, bool Loop, bool HopsMaxed);
}

/// <summary>
/// Wire body for the NPS-CR-0009 federated cluster tuple
/// <c>(cluster_anchor, cluster_epoch, active_nid)</c> (NDP §9).
/// </summary>
public sealed record ClusterTupleBody
{
    /// <summary>The cluster's stable <c>cluster_anchor</c> NID.</summary>
    [JsonPropertyName("cluster_anchor")]
    public string? ClusterAnchor { get; init; }

    /// <summary>The ownership fence. Absent ⇒ <c>1</c> (single-Anchor cluster).</summary>
    [JsonPropertyName("cluster_epoch")]
    public ulong? ClusterEpoch { get; init; }

    /// <summary>NID of the Anchor holding ownership at <see cref="ClusterEpoch"/>.</summary>
    [JsonPropertyName("active_nid")]
    public string? ActiveNid { get; init; }
}

/// <summary>Response body of <c>GET /v1/cluster/resolve</c>.</summary>
public sealed record ClusterResolveResponse
{
    /// <summary>The cluster that was resolved.</summary>
    [JsonPropertyName("cluster_anchor")]
    public required string ClusterAnchor { get; init; }

    /// <summary>The winning (highest) <c>cluster_epoch</c>.</summary>
    [JsonPropertyName("cluster_epoch")]
    public required ulong ClusterEpoch { get; init; }

    /// <summary>NID of the cluster's current active Anchor.</summary>
    [JsonPropertyName("active_nid")]
    public required string ActiveNid { get; init; }

    /// <summary><c>local</c>, or the federated peer NID the tuple was learnt from.</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    /// <summary>The active Anchor's endpoint, when it is announced to this registry.</summary>
    [JsonPropertyName("resolved")]
    public NdpResolveResult? Resolved { get; init; }
}
