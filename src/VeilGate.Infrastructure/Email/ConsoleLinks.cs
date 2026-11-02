using Microsoft.Extensions.Options;
using VeilGate.Application.Abstractions;

namespace VeilGate.Infrastructure.Email;

public sealed class ConsoleLinks(IOptions<ConsoleOptions> options) : IConsoleLinks
{
    public string AcceptInvitation(string token)
    {
        var baseUrl = options.Value.BaseUrl.Trim().TrimEnd('/');
        if (baseUrl.Length == 0) throw new InvalidOperationException("Console:BaseUrl is required to build links.");
        return $"{baseUrl}/accept-invite?token={Uri.EscapeDataString(token)}";
    }
}