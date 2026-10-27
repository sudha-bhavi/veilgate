using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using VeilGate.Application.Abstractions;
using VeilGate.Domain.Common;

namespace VeilGate.Infrastructure.Llm;

/// <summary>
/// Checks a provider address when it is saved (Backend doc 5.1): https, no credentials in the address, and the name resolves only to
/// public addresses, never loopback, private, link-local or the cloud metadata address. If ANY answer is forbidden the address is refused.
/// This is the early, friendly check. The one that cannot be dodged runs on every connection (<see cref="SsrfGuard.ConnectAsync"/>).
/// In Development (<see cref="LlmOptions.AllowPrivateNetworks"/>) plain http and local addresses are allowed so a mock model can be used.
/// </summary>
public sealed class OutboundUrlValidator(IOptions<LlmOptions> options) : IOutboundUrlValidator
{
    public async Task<Result> ValidateAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0)
            return Result.Failure(Invalid("provider.url_invalid", "Invalid provider URL."));

        var allowPrivate = options.Value.AllowPrivateNetworks;
        if (uri.Scheme != Uri.UriSchemeHttps && !(allowPrivate && uri.Scheme == Uri.UriSchemeHttp))
            return Result.Failure(Invalid("provider.url_invalid", "The provider URL must use https."));

        if (allowPrivate) return Result.Success();

        IPAddress[] addresses;
        try
        {
            // A literal address needs no lookup; a name is resolved the same way the connection will resolve it.
            addresses = IPAddress.TryParse(uri.DnsSafeHost, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct);
        }
        catch (SocketException)
        {
            return Result.Failure(Invalid("provider.url_unresolvable", "The provider address could not be found."));
        }

        if (addresses.Length == 0 || addresses.Any(SsrfGuard.IsForbidden))
            return Result.Failure(Invalid("provider.url_forbidden", "The provider address points to a private or internal network."));
        return Result.Success();
    }

    private static Error Invalid(string code, string message) => Error.Validation(code, message);
}
