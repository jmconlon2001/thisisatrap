using System.Net;
using System.Net.Sockets;

namespace ThisIsATrap.Analysis;

/// <summary>Thrown when an outbound connection is refused by <see cref="OutboundPolicy"/>.</summary>
public sealed class OutboundBlockedException(string message) : IOException(message);

/// <summary>
/// Guards the app's only outbound HTTP (link checks and webhooks) against SSRF.
/// The check runs inside the socket connect callback, against the addresses DNS actually
/// returned, so it also covers redirects and DNS rebinding.
/// </summary>
public static class OutboundPolicy
{
    /// <summary>Why <paramref name="ip"/> may not be contacted, or null if it may.</summary>
    public static string? Reject(IPAddress ip, bool allowPrivate)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (HostClassifier.IsMetadata(ip)) return $"{ip} is a cloud metadata address";
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast)) return $"{ip} is not a routable address";
        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.IsIPv6Multicast) return $"{ip} is a multicast address";
        if (ip.AddressFamily == AddressFamily.InterNetwork && ip.GetAddressBytes()[0] >= 224) return $"{ip} is a multicast/reserved address";
        if (!allowPrivate && HostClassifier.IsPrivate(ip)) return $"{ip} is on a private network";
        return null;
    }

    public static SocketsHttpHandler CreateHandler(bool allowPrivate) => new()
    {
        AllowAutoRedirect = false,           // redirects are followed by the caller so each hop is recorded
        UseCookies = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
            string? reason = null;
            foreach (var ip in addresses)
            {
                reason = Reject(ip, allowPrivate);
                if (reason is not null) continue;
                var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
            throw new OutboundBlockedException(reason ?? $"{host} did not resolve to any address");
        },
    };
}
