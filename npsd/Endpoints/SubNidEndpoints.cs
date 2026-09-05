// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.Daemon.Npsd.SubNids;
using NPS.NIP.Ca;

namespace NPS.Daemon.Npsd.Endpoints;

/// <summary>
/// Sub-NID issuance endpoints — POST/GET/RENEW/REVOKE/LIST.
/// </summary>
/// <remarks>
/// Clients URL-encode the NID in path segments. ASP.NET Core decodes
/// path segments before routing, so <c>/v1/agents/urn%3Anps%3Ahost%3A...</c>
/// matches the route <c>/v1/agents/{nid}</c>.
/// </remarks>
public static class SubNidEndpoints
{
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder MapSubNidEndpoints(this IEndpointRouteBuilder app)
    {
        // POST /v1/agents — issue a new sub-NID for a local agent.
        app.MapPost("/v1/agents", async (HttpContext ctx, SubNidService svc) =>
        {
            IssueRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<IssueRequest>(ctx.Request.Body, s_jsonOpts, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                return ProblemBadFrame($"Request body is not valid JSON: {ex.Message}");
            }
            if (body is null) return ProblemBadFrame("Request body is required.");
            if (body.Capabilities is null || body.Capabilities.Count == 0)
                return ProblemBadFrame("`capabilities` is required and must be non-empty.");

            try
            {
                var result = svc.Issue(
                    identifier:        body.Identifier,
                    capabilities:      body.Capabilities,
                    scopeJson:         body.Scope is null ? "{}" : body.Scope.Value.GetRawText(),
                    agentPubKeyEncoded:body.AgentPubKey,
                    metadataJson:      body.Metadata is null ? null : body.Metadata.Value.GetRawText());

                return Results.Json(new IssueResponse(
                    Frame:             result.Frame,
                    MintedPrivateKey:  result.MintedPrivateKeyBase64Url is null
                                          ? null
                                          : $"ed25519-raw:{result.MintedPrivateKeyBase64Url}"),
                    s_jsonOpts,
                    statusCode: 201);
            }
            catch (SubNidAlreadyExistsException ex)
            {
                return Results.Json(new
                {
                    error   = NipErrorCodes.NidAlreadyExists,
                    status  = "NPS-CLIENT-CONFLICT",
                    message = ex.Message,
                    nid     = ex.Nid,
                }, s_jsonOpts, statusCode: 409);
            }
            catch (ArgumentException ex)
            {
                return ProblemBadFrame(ex.Message);
            }
        });

        // GET /v1/agents — list issued sub-NIDs.
        app.MapGet("/v1/agents", (int? limit, int? offset, SubNidService svc) =>
        {
            var l = Math.Clamp(limit ?? 100, 1, 500);
            var o = Math.Max(0, offset ?? 0);
            var rows = svc.List(l, o)
                .Select(r => new ListEntry(
                    r.Nid, r.Serial, r.IssuedAt.ToString("O"), r.ExpiresAt.ToString("O"),
                    r.Revoked, r.Capabilities.Split(',', StringSplitOptions.RemoveEmptyEntries)))
                .ToArray();
            return Results.Json(new { agents = rows, count = rows.Length, limit = l, offset = o }, s_jsonOpts);
        });

        // GET /v1/agents/{nid}
        app.MapGet("/v1/agents/{nid}", (string nid, SubNidService svc) =>
        {
            var record = svc.Get(nid);
            if (record is null)
                return Results.Json(new
                {
                    error   = NipErrorCodes.NidNotFound,
                    status  = "NPS-CLIENT-NOT-FOUND",
                    nid,
                }, s_jsonOpts, statusCode: 404);

            return Results.Json(new
            {
                nid           = record.Nid,
                pub_key       = record.PubKey,
                issued_by     = record.IssuedBy,
                issued_at     = record.IssuedAt.ToString("O"),
                expires_at    = record.ExpiresAt.ToString("O"),
                serial        = record.Serial,
                capabilities  = record.Capabilities.Split(',', StringSplitOptions.RemoveEmptyEntries),
                scope         = JsonSerializer.Deserialize<JsonElement>(record.ScopeJson),
                metadata      = record.MetadataJson is null ? null : (JsonElement?)JsonSerializer.Deserialize<JsonElement>(record.MetadataJson),
                revoked       = record.Revoked,
                revoked_at    = record.RevokedAt?.ToString("O"),
                revoke_reason = record.RevokeReason,
            }, s_jsonOpts);
        });

        // POST /v1/agents/{nid}/renew — replace an eligible short-lived credential.
        app.MapPost("/v1/agents/{nid}/renew", (string nid, SubNidService svc) =>
        {
            try
            {
                return Results.Json(
                    new RenewResponse(svc.Renew(nid)),
                    s_jsonOpts,
                    statusCode: 200);
            }
            catch (SubNidRenewalException ex)
            {
                var statusCode = ex.ErrorCode switch
                {
                    NipErrorCodes.NidNotFound => 404,
                    NipErrorCodes.RenewalTooEarly => 400,
                    NipErrorCodes.CertRevoked or NipErrorCodes.CertExpired => 401,
                    _ => 409,
                };
                var status = statusCode switch
                {
                    404 => "NPS-CLIENT-NOT-FOUND",
                    401 => "NPS-AUTH-UNAUTHENTICATED",
                    400 => "NPS-CLIENT-BAD-FRAME",
                    _ => "NPS-CLIENT-CONFLICT",
                };
                return Results.Json(new
                {
                    error = ex.ErrorCode,
                    status,
                    message = ex.Message,
                    nid = ex.Nid,
                }, s_jsonOpts, statusCode: statusCode);
            }
        });

        // POST /v1/agents/{nid}/revoke
        app.MapPost("/v1/agents/{nid}/revoke", async (string nid, HttpContext ctx, SubNidService svc) =>
        {
            RevokeRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<RevokeRequest>(ctx.Request.Body, s_jsonOpts, ctx.RequestAborted);
            }
            catch (JsonException) { body = null; }
            var reason = body?.Reason ?? "unspecified";

            return svc.Revoke(nid, reason)
                ? Results.NoContent()
                : Results.Json(new
                {
                    error   = NipErrorCodes.NidNotFound,
                    status  = "NPS-CLIENT-NOT-FOUND",
                    nid,
                    message = "Already revoked, or never issued.",
                }, s_jsonOpts, statusCode: 404);
        });

        return app;
    }

    private static IResult ProblemBadFrame(string message) =>
        Results.Json(new
        {
            error   = "NIP-IDENT-BAD-REQUEST",
            status  = "NPS-CLIENT-BAD-FRAME",
            message,
        }, s_jsonOpts, statusCode: 400);

    // ── DTOs ──────────────────────────────────────────────────────────────────

    public sealed record IssueRequest(
        string?               Identifier,
        IReadOnlyList<string> Capabilities,
        JsonElement?          Scope,
        string?               AgentPubKey,
        JsonElement?          Metadata);

    public sealed record IssueResponse(
        NPS.NIP.Frames.IdentFrame Frame,
        string?                   MintedPrivateKey);

    public sealed record RenewResponse(NPS.NIP.Frames.IdentFrame Frame);

    public sealed record RevokeRequest(string? Reason);

    public sealed record ListEntry(
        string   Nid,
        string   Serial,
        string   IssuedAt,
        string   ExpiresAt,
        bool     Revoked,
        string[] Capabilities);
}
