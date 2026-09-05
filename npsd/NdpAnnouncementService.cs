// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NPS.Daemon.Npsd.SubNids;
using NPS.NDP.Frames;
using NPS.NDP.Registry;
using NPS.NIP.Crypto;
using NSec.Cryptography;

namespace NPS.Daemon.Npsd;

/// <summary>
/// Emits signed ephemeral AnnounceFrames for npsd-managed local agents.
/// Caller-managed (BYO) keys are deliberately excluded because npsd cannot
/// sign a publisher announcement without possessing that publisher's key.
/// </summary>
public sealed class NdpAnnouncementService : BackgroundService
{
    private readonly NpsdOptions _options;
    private readonly SubNidStore _store;
    private readonly AgentKeyProtector _keyProtector;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<NdpAnnouncementService> _logger;

    public int EffectiveTtlSeconds => Math.Clamp(_options.NdpAnnounceTtlSeconds, 1, 60);

    public int EffectiveIntervalSeconds => Math.Min(
        Math.Max(1, _options.NdpAnnounceIntervalSeconds),
        Math.Max(1, EffectiveTtlSeconds / 2));

    public string EffectiveAdvertiseHost => _options.NdpAdvertiseHost ?? _options.Host;

    public bool HasRoutableAdvertiseHost =>
        EffectiveAdvertiseHost is not "0.0.0.0" and not "::" and not "[::]";

    public NdpAnnouncementService(
        NpsdOptions options,
        SubNidStore store,
        AgentKeyProtector keyProtector,
        IHttpClientFactory httpClientFactory,
        ILogger<NdpAnnouncementService> logger)
    {
        _options = options;
        _store = store;
        _keyProtector = keyProtector;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.NdpAnnounceEnabled)
            return;
        if (!HasRoutableAdvertiseHost)
        {
            _logger.LogWarning(
                "NDP announcements are disabled because bind host {Host} is not routable; set NPSD_NDP_ADVERTISE_HOST.",
                EffectiveAdvertiseHost);
            return;
        }

        await PublishSafelyAsync((uint)EffectiveTtlSeconds, stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(EffectiveIntervalSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await PublishSafelyAsync((uint)EffectiveTtlSeconds, stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_options.NdpAnnounceEnabled && HasRoutableAdvertiseHost)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await PublishSafelyAsync(ttl: 0, timeout.Token);
        }
        await base.StopAsync(cancellationToken);
    }

    private async Task PublishSafelyAsync(uint ttl, CancellationToken cancellationToken)
    {
        try
        {
            var published = await PublishOnceAsync(
                _httpClientFactory.CreateClient("npsd-ndp"), ttl, cancellationToken);
            if (published > 0)
                _logger.LogDebug("Published {Count} signed NDP agent announcements (ttl={Ttl}).", published, ttl);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown or the bounded graceful-offline attempt expired.
        }
        catch (Exception ex)
        {
            // Registry availability must not take down local issuance/inbox service.
            _logger.LogWarning(ex, "NDP announcement cycle failed; retrying on the next interval.");
        }
    }

    internal async Task<int> PublishOnceAsync(
        HttpClient client,
        uint ttl,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var records = _store.ListAnnounceable(now);
        var published = 0;
        var endpoint = new Uri(
            new Uri(_options.NdpRegistryUrl.TrimEnd('/') + "/", UriKind.Absolute),
            "v1/announce");

        foreach (var record in records)
        {
            try
            {
                var sequence = _store.NextGraphSequence(record.Nid, now);
                if (sequence is null)
                    continue;

                var frame = CreateSignedFrame(record, sequence.Value, ttl, now);
                using var response = await client.PostAsJsonAsync(
                    endpoint, frame, s_jsonOptions, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Registry rejected AnnounceFrame for {Nid} with HTTP {StatusCode}.",
                        record.Nid,
                        (int)response.StatusCode);
                    continue;
                }
                published++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not publish AnnounceFrame for {Nid}; continuing with other agents.",
                    record.Nid);
            }
        }

        return published;
    }

    internal AnnounceFrame CreateSignedFrame(
        SubNidRecord record,
        ulong graphSequence,
        uint ttl,
        DateTimeOffset now)
    {
        if (record.PrivKeyEncrypted is null)
            throw new InvalidOperationException("Cannot sign an announcement for a caller-managed key.");

        var timestamp = now.ToUniversalTime().ToString("O");
        var unsigned = new AnnounceFrame
        {
            Nid = record.Nid,
            Addresses =
            [
                new NdpAddress
                {
                    Host = EffectiveAdvertiseHost,
                    Port = _options.Port,
                    Protocol = "nps-native",
                },
            ],
            Capabilities = record.Capabilities
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Ttl = ttl,
            HeartbeatIntervalMs = checked((uint)EffectiveIntervalSeconds * 1000),
            Timestamp = timestamp,
            LastSeen = timestamp,
            Health = ttl == 0 ? "draining" : "healthy",
            GraphSeq = graphSequence,
            ActivationMode = "ephemeral",
            Signature = string.Empty,
        };

        using var key = _keyProtector.Unprotect(record.Nid, record.PrivKeyEncrypted);
        var element = JsonSerializer.SerializeToElement(unsigned, s_jsonOptions);
        var canonical = Encoding.UTF8.GetBytes(NdpAnnounceCanonicalizer.CanonicalJson(element));
        var signature = SignatureAlgorithm.Ed25519.Sign(key, canonical);
        return unsigned with { Signature = "ed25519:" + NipSigner.Base64Url(signature) };
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
