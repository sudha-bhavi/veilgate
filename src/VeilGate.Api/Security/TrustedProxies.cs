using Microsoft.AspNetCore.HttpOverrides;

namespace VeilGate.Api.Security;

/// <summary>
/// Behind a reverse proxy (Caddy, Nginx, a cloud load balancer) every request arrives from the proxy's own address, and the real
/// caller's address travels in the X-Forwarded-For header. Without this, the sign-in limit would be shared by every user at once,
/// API clients' IP allowlists would compare the proxy's address, and audit and security events would name the proxy as the source.
///
/// But anyone can send an X-Forwarded-For header, so it is believed ONLY when the request really comes from a configured proxy.
/// Settings: Proxy:TrustedNetworks (CIDR ranges such as 172.16.0.0/12) and/or Proxy:TrustedAddresses (single addresses).
/// With neither, the headers are ignored, which is right when the API is exposed directly.
/// </summary>
public static class TrustedProxies
{
    public const string Section = "Proxy";

    /// <summary>Returns true when at least one trusted proxy is configured, so the caller knows to add UseForwardedHeaders().</summary>
    public static bool AddTrustedProxies(this IServiceCollection services, IConfiguration configuration)
    {
        var networks = Values(configuration, "TrustedNetworks").Select(ParseNetwork).ToList();
        var addresses = Values(configuration, "TrustedAddresses").Select(ParseAddress).ToList();
        if (networks.Count == 0 && addresses.Count == 0) return false;

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // Believe only the hop nearest to us. Entries a client put at the front of the header are never used.
            options.ForwardLimit = 1;
            options.KnownNetworks.Clear();   // the defaults trust loopback; we trust exactly what is configured
            options.KnownProxies.Clear();
            foreach (var network in networks) options.KnownNetworks.Add(network);
            foreach (var address in addresses) options.KnownProxies.Add(address);
        });
        return true;
    }

    private static IEnumerable<string> Values(IConfiguration configuration, string name) =>
        configuration.GetSection($"{Section}:{name}").GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim());

    public static Microsoft.AspNetCore.HttpOverrides.IPNetwork ParseNetwork(string text)
    {
        var parts = text.Split('/');
        if (parts.Length != 2 || !System.Net.IPAddress.TryParse(parts[0], out var address) || !int.TryParse(parts[1], out var prefix)
            || prefix < 0 || prefix > (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128))
            throw new InvalidOperationException($"{Section}:TrustedNetworks has an entry that is not a network such as 172.16.0.0/12.");

        return new Microsoft.AspNetCore.HttpOverrides.IPNetwork(address, prefix);
    }

    public static System.Net.IPAddress ParseAddress(string text) =>
        System.Net.IPAddress.TryParse(text, out var address)
            ? address
            : throw new InvalidOperationException($"{Section}:TrustedAddresses has an entry that is not an IP address.");
}
