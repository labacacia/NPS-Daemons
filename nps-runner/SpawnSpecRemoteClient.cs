// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;

namespace NPS.Daemon.Runner;

/// <summary>
/// HTTPS transport for remote SpawnSpec documents. DNS answers are validated
/// immediately before connecting so the request cannot be rebound to a private address.
/// </summary>
internal sealed class SpawnSpecRemoteClient : IDisposable
{
    public SpawnSpecRemoteClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = ConnectPublicAsync,
        };
        Client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
    }

    public HttpClient Client { get; }

    public void Dispose() => Client.Dispose();

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return IsPublicAddress(address.MapToIPv4());

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return !IPAddress.IPv6Any.Equals(address) &&
                   !IPAddress.IPv6Loopback.Equals(address) &&
                   !address.IsIPv6LinkLocal &&
                   !address.IsIPv6SiteLocal &&
                   !address.IsIPv6Multicast &&
                   (bytes[0] & 0xfe) != 0xfc;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var octets = address.GetAddressBytes();
        return octets[0] != 0 &&
               octets[0] != 10 &&
               octets[0] != 127 &&
               !(octets[0] == 100 && octets[1] is >= 64 and <= 127) &&
               !(octets[0] == 169 && octets[1] == 254) &&
               !(octets[0] == 172 && octets[1] is >= 16 and <= 31) &&
               !(octets[0] == 192 && octets[1] == 0 && octets[2] is 0 or 2) &&
               !(octets[0] == 192 && octets[1] == 168) &&
               !(octets[0] == 198 && octets[1] is 18 or 19) &&
               !(octets[0] == 198 && octets[1] == 51 && octets[2] == 100) &&
               !(octets[0] == 203 && octets[1] == 0 && octets[2] == 113) &&
               octets[0] < 224;
    }

    private static async ValueTask<Stream> ConnectPublicAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(
            context.DnsEndPoint.Host,
            cancellationToken);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
        {
            throw new HttpRequestException(
                "SpawnSpec destination did not resolve exclusively to public addresses.");
        }

        Exception? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(address, context.DnsEndPoint.Port),
                    cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (
                ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                lastError = ex;
                if (ex is OperationCanceledException)
                    throw;
            }
        }

        throw new HttpRequestException(
            "Could not connect to a validated SpawnSpec destination.",
            lastError);
    }
}
