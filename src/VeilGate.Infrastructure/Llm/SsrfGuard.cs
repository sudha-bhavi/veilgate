using System.Net;
using System.Net.Sockets;

namespace VeilGate.Infrastructure.Llm;

/// <summary>
/// Server-side request forgery defense. A provider URL is configuration, but if it ever becomes editable (it will, in M4) an
/// attacker could point it at http://169.254.169.254 (cloud credentials) or an internal service.
/// The check runs on the address we actually connect to, on every connection, so a hostname that
/// resolves to a public address when saved and a private one later ("DNS rebinding") is still stopped.
/// </summary>
public static class SsrfGuard
{
    public static bool IsForbidden(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var b = address.GetAddressBytes();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return b[0] == 0                                  // "this network", 0.0.0.0
                || b[0] == 10                                 // private
                || (b[0] == 100 && b[1] is >= 64 and <= 127)  // carrier-grade NAT
                || (b[0] == 169 && b[1] == 254)               // link-local, includes the cloud metadata address
                || (b[0] == 172 && b[1] is >= 16 and <= 31)   // private
                || (b[0] == 192 && b[1] == 168)               // private
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)    // IETF reserved
                || b[0] >= 224;                               // multicast, reserved, broadcast
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.Equals(IPAddress.IPv6Any)
                || address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast
                || (b[0] & 0xFE) == 0xFC;                     // unique local addresses, fc00::/7
        }

        return true;   // any other address family: refuse
    }

    /// <summary>Connects like the default handler, but only to addresses that pass <see cref="IsForbidden"/>.</summary>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        var allowed = addresses.Where(a => !IsForbidden(a)).ToArray();
        if (allowed.Length == 0) throw new HttpRequestException("The destination address is not allowed.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
