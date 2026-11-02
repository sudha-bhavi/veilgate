using Microsoft.Extensions.Logging;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;

namespace VeilGate.Application.Users;

/// <summary>Sends the invitation email. A failure is logged and reported as "false", never thrown: the invitation itself is already saved.</summary>
public sealed class InvitationMailer(IUserRepository users, IEmailSender email, IConsoleLinks links, ILogger<InvitationMailer> logger)
{
    public async Task<bool> SendAsync(string to, UserRole role, DateTimeOffset expiresAt, string token, Guid inviterId, CancellationToken ct)
    {
        try
        {
            var inviter = await users.GetCurrentTenantUserAsync(inviterId, ct);
            var tenantName = await users.GetTenantNameAsync(ct);
            var message = InvitationEmail.Build(
                to, tenantName ?? "your organization", inviter?.DisplayName ?? "An administrator", role, links.AcceptInvitation(token), expiresAt);
            await email.SendAsync(message, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Only the kind of error is logged: the message can contain the address, and nothing here should.
            logger.LogError("Could not send the invitation email: {ExceptionType}", ex.GetType().Name);
            return false;
        }
    }
}

/// <summary>
/// Turns the token from an invitation link into the invitation, or into null. "Unknown", "already used" and "expired" all look the same,
/// so a link tells a stranger nothing. As a side effect it sets the tenant for the request, because nobody is signed in on this page.
/// </summary>
public sealed class InvitationFinder(IInvitationRepository invitations, ITenantContext tenant, IClock clock)
{
    public async Task<Invitation?> FindUsableAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128) return null;   // real tokens are 43 characters

        var hash = RefreshTokens.Hash(token);
        var tenantId = await invitations.FindTenantByTokenHashAsync(hash, ct);
        if (tenantId is null) return null;
        if (!tenant.IsSet) tenant.Set(tenantId.Value);

        var invitation = await invitations.FindByTokenHashAsync(hash, ct);
        return invitation is not null && invitation.IsUsable(clock.UtcNow) ? invitation : null;
    }
}