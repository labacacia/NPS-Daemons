// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using System.Text.Json;

namespace NPS.Daemon.Runner;

internal sealed record SpawnSpecResolution(SpawnSpec? Spec, string? ErrorCode)
{
    public bool Success => Spec is not null && ErrorCode is null;
}

/// <summary>
/// Resolves CR-0007 inline, HTTPS, and NWP SpawnSpec references, then applies
/// the same portable schema parser used by direct inbox messages.
/// </summary>
internal sealed class SpawnSpecResolver
{
    internal const int MaxContentBytes = 64 * 1024;
    internal const int MaxRedirects = 5;
    private const string Prefix = "spawnspec:";
    private readonly HttpClient registryHttp;
    private readonly HttpClient remoteHttp;
    private readonly RunnerOptions options;

    internal SpawnSpecResolver(HttpClient http, RunnerOptions options)
        : this(http, http, options)
    {
    }

    internal SpawnSpecResolver(
        HttpClient registryHttp,
        SpawnSpecRemoteClient remoteHttp,
        RunnerOptions options)
        : this(registryHttp, remoteHttp.Client, options)
    {
    }

    private SpawnSpecResolver(
        HttpClient registryHttp,
        HttpClient remoteHttp,
        RunnerOptions options)
    {
        this.registryHttp = registryHttp;
        this.remoteHttp = remoteHttp;
        this.options = options;
    }

    public async Task<SpawnSpecResolution> ResolveEnvelopeAsync(
        string json,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("spawn_spec_ref", out var referenceElement))
            {
                return SpawnSpecParser.TryParse(json, out var direct, out var directError)
                    ? new SpawnSpecResolution(direct, null)
                    : new SpawnSpecResolution(null, directError);
            }

            if (referenceElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(referenceElement.GetString()))
            {
                return Invalid();
            }

            var content = await ResolveContentAsync(
                referenceElement.GetString()!,
                cancellationToken);
            if (content is null ||
                !SpawnSpecParser.TryParse(content, out var resolved, out _))
            {
                return Invalid();
            }

            var taskId = OptionalString(root, "task_id");
            var replyTo = OptionalString(root, "reply_to");
            return new SpawnSpecResolution(
                resolved! with
                {
                    TaskId = string.IsNullOrWhiteSpace(taskId) ? resolved.TaskId : taskId,
                    ReplyTo = replyTo ?? resolved.ReplyTo,
                },
                null);
        }
        catch (JsonException)
        {
            return Invalid();
        }
        catch (HttpRequestException)
        {
            return Invalid();
        }
        catch (InvalidOperationException)
        {
            return Invalid();
        }
        catch (KeyNotFoundException)
        {
            return Invalid();
        }
    }

    private async Task<string?> ResolveContentAsync(
        string reference,
        CancellationToken cancellationToken)
    {
        if (reference.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return DecodeInline(reference[Prefix.Length..]);

        if (!Uri.TryCreate(reference, UriKind.Absolute, out var uri))
            return null;

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return await FetchJsonAsync(uri, cancellationToken);

        if (!string.Equals(uri.Scheme, "nwp", StringComparison.OrdinalIgnoreCase))
            return null;

        var endpoint = await ResolveNwpEndpointAsync(uri, cancellationToken);
        return endpoint is null
            ? null
            : await FetchJsonAsync(endpoint, cancellationToken);
    }

    private async Task<Uri?> ResolveNwpEndpointAsync(
        Uri target,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(options.RegistryUrl, UriKind.Absolute, out var registry) ||
            registry.Scheme is not ("http" or "https"))
        {
            return null;
        }

        var request = new UriBuilder(registry)
        {
            Path = JoinPath(registry.AbsolutePath, "v1/resolve"),
            Query = $"target={Uri.EscapeDataString(target.AbsoluteUri)}",
        }.Uri;
        using var response = await registryHttp.GetAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);
        var resolved = document.RootElement.GetProperty("resolved");
        var host = resolved.GetProperty("host").GetString();
        var port = resolved.GetProperty("port").GetInt32();
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535)
            return null;

        return new UriBuilder(
            Uri.UriSchemeHttps,
            host,
            port,
            target.AbsolutePath,
            target.Query).Uri;
    }

    private async Task<string?> FetchJsonAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        var current = uri;
        for (var redirects = 0; redirects <= MaxRedirects; redirects++)
        {
            if (!DestinationShapeIsAllowed(current))
                return null;

            using var response = await remoteHttp.GetAsync(
                current,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (IsRedirect(response.StatusCode))
            {
                if (redirects == MaxRedirects ||
                    response.Headers.Location is not { } location)
                {
                    return null;
                }

                current = location.IsAbsoluteUri
                    ? location
                    : new Uri(current, location);
                continue;
            }

            if (!response.IsSuccessStatusCode ||
                response.Content.Headers.ContentLength is > MaxContentBytes)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[MaxContentBytes + 1];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(total, buffer.Length - total),
                    cancellationToken);
                if (read == 0)
                    break;
                total += read;
            }
            return total <= MaxContentBytes
                ? Encoding.UTF8.GetString(buffer, 0, total)
                : null;
        }

        return null;
    }

    private static bool DestinationShapeIsAllowed(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            string.IsNullOrWhiteSpace(uri.IdnHost) ||
            string.Equals(uri.IdnHost, "localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.IdnHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !IPAddress.TryParse(uri.IdnHost, out var address) ||
               SpawnSpecRemoteClient.IsPublicAddress(address);
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.Moved or
            HttpStatusCode.Redirect or
            HttpStatusCode.RedirectMethod or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;

    private static string? DecodeInline(string encoded)
    {
        try
        {
            if (string.IsNullOrEmpty(encoded))
                return null;
            var padded = encoded
                .Replace('-', '+')
                .Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            var bytes = Convert.FromBase64String(padded);
            return bytes.Length <= MaxContentBytes
                ? Encoding.UTF8.GetString(bytes)
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? OptionalString(JsonElement root, string name)
        => root.TryGetProperty(name, out var element) &&
           element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static string JoinPath(string basePath, string suffix)
        => $"{basePath.TrimEnd('/')}/{suffix}";

    private static SpawnSpecResolution Invalid()
        => new(null, SpawnSpecParser.InvalidCode);
}
