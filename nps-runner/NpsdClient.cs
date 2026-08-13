// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NPS.Daemon.Runner;

/// <summary>
/// Thin HTTP client over the local npsd REST API.
/// Covers: agent self-registration, inbox poll, ack, and completion notify.
/// </summary>
internal sealed class NpsdClient(HttpClient http, RunnerOptions opts)
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private string Base => opts.NpsdUrl.TrimEnd('/');

    // ── Registration ────────────────────────────────────────────────────────

    /// <summary>
    /// Ensures this runner is registered as a sub-NID on npsd.
    /// Idempotent: on 409 reads the NID from the conflict body.
    /// Returns the assigned NID string.
    /// </summary>
    public async Task<string> EnsureRegisteredAsync(CancellationToken ct)
    {
        var body = new { identifier = opts.AgentId, capabilities = new[] { "spawn" } };

        using var resp = await http.PostAsJsonAsync($"{Base}/v1/agents", body, s_json, ct);

        if (resp.IsSuccessStatusCode)
        {
            var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(s_json, ct);
            return doc.GetProperty("frame").GetProperty("nid").GetString()
                   ?? throw new InvalidOperationException("npsd returned null NID in IssueResponse");
        }

        if ((int)resp.StatusCode == 409)
        {
            // Conflict body includes the nid directly.
            var conflict = await resp.Content.ReadFromJsonAsync<JsonElement>(s_json, ct);
            if (conflict.TryGetProperty("nid", out var nidEl) && nidEl.GetString() is { } nid)
                return nid;

            throw new InvalidOperationException(
                $"Runner NID not found after 409 (agent_id={opts.AgentId}); body: {conflict}");
        }

        var err = await resp.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException(
            $"POST /v1/agents returned HTTP {(int)resp.StatusCode}: {err}");
    }

    // ── Inbox ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Long-polls the inbox for <paramref name="waitSec"/> seconds.
    /// Returns up to <paramref name="batch"/> messages (may be empty on timeout).
    /// </summary>
    public async Task<InboxPollMessage[]> PollAsync(
        string nid, int waitSec, int batch, CancellationToken ct)
    {
        var url = $"{Base}/v1/inbox/{Uri.EscapeDataString(nid)}?wait={waitSec}&batch={batch}";
        var doc = await http.GetFromJsonAsync<JsonElement>(url, s_json, ct);

        var arr = doc.GetProperty("messages");
        var result = new InboxPollMessage[arr.GetArrayLength()];
        int i = 0;
        foreach (var m in arr.EnumerateArray())
        {
            result[i++] = new InboxPollMessage(
                MessageId: m.GetProperty("message_id").GetString()!,
                ContentType: m.GetProperty("content_type").GetString() ?? "application/octet-stream",
                PayloadB64: m.GetProperty("payload_b64").GetString()!);
        }
        return result;
    }

    /// <summary>Acknowledges (removes) a message from the inbox.</summary>
    public async Task AckAsync(string nid, string messageId, CancellationToken ct)
    {
        var url = $"{Base}/v1/inbox/{Uri.EscapeDataString(nid)}/{messageId}";
        await http.DeleteAsync(url, ct);
    }

    /// <summary>
    /// Posts a <see cref="CompletionNotification"/> as JSON to <paramref name="replyToNid"/>'s inbox.
    /// Best-effort: caller should catch and log, not propagate.
    /// </summary>
    public async Task NotifyAsync(string replyToNid, CompletionNotification note, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(note, s_json);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var url = $"{Base}/v1/inbox/{Uri.EscapeDataString(replyToNid)}";
        using var _ = await http.PostAsync(url, content, ct);
    }
}

internal sealed record InboxPollMessage(string MessageId, string ContentType, string PayloadB64);
