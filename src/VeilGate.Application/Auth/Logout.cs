using VeilGate.Application.Abstractions;
using VeilGate.Domain;

namespace VeilGate.Application.Auth;

/// <param name="Ip">The caller's address, for the audit log only.</param>
public sealed record LogoutCommand(string? RefreshToken, string? Ip = null);

/// <summary>
/// Ends the session behind this refresh token. Always "succeeds": logging out twice, or with no session, is fine.
/// Only a sign-out that really ended a session is audited.
/// </summary>
public sealed class LogoutHandler(ISessionRepository sessions, ITenantContext tenant, IClock clock, AuthAuditor auditor)
{
    public async Task HandleAsync(LogoutCommand cmd, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.RefreshToken)) return;
        var hash = RefreshTokens.Hash(cmd.RefreshToken);

        var tenantId = await sessions.FindTenantByRefreshHashAsync(hash, ct);
        if (tenantId is null) return;
        if (!tenant.IsSet) tenant.Set(tenantId.Value);

        var session = await sessions.FindByRefreshHashAsync(hash, ct);
        if (session is null) return;

        session.Revoke("logout", clock.UtcNow);
        await sessions.SaveChangesAsync(ct);
        await auditor.SucceededAsync(tenant.TenantId, session.UserId, AuthAuditor.SignOut, cmd.Ip, null, ct);
    }
}