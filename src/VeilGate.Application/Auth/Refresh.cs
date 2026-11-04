using Microsoft.Extensions.Logging;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.Domain.Audit;
using VeilGate.Domain.Common;

namespace VeilGate.Application.Auth;

public sealed record RefreshCommand(string? RefreshToken, string? Ip = null);

public sealed record RefreshResult(string AccessToken, string RefreshToken, DateTimeOffset RefreshExpiresAt);

/// <summary>Swaps a refresh token for a new access token AND a new refresh token (rotation). A replayed old token ends the session.</summary>
public sealed class RefreshHandler(
    ISessionRepository sessions, IUserRepository users, ITenantContext tenant, ITokenService tokens, IClock clock,
    ISecurityEventWriter securityEvents, ILogger<RefreshHandler> logger)
{
    public async Task<Result<RefreshResult>> HandleAsync(RefreshCommand cmd, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.RefreshToken)) return Session.RefreshInvalid;
        var presented = RefreshTokens.Hash(cmd.RefreshToken);

        // No access token here, so we don't know the tenant yet. The lookup function tells us; then RLS works as usual.
        var tenantId = await sessions.FindTenantByRefreshHashAsync(presented, ct);
        if (tenantId is null) return Session.RefreshInvalid;
        if (!tenant.IsSet) tenant.Set(tenantId.Value);

        var session = await sessions.FindByRefreshHashAsync(presented, ct);
        if (session is null) return Session.RefreshInvalid;

        var now = clock.UtcNow;
        var user = await users.GetCurrentTenantUserAsync(session.UserId, ct);
        if (user is null || user.Status != UserStatus.Active || !await users.IsTenantActiveAsync(ct))
        {
            // Disabled user or suspended tenant: this session must not live on.
            session.Revoke("user_inactive", now);
            await sessions.SaveChangesAsync(ct);
            return Session.RefreshInvalid;
        }

        var newToken = RefreshTokens.Generate();
        var newHash = RefreshTokens.Hash(newToken);

        if (session.IsActive(now) && session.IsCurrentToken(presented))
        {
            // The newest token. Swap it in ONE database step: if another request swapped it a moment ago, this one lost, which means the
            // same token was presented twice. That is a replay, handled like any other (read, rotate in memory, save would hand a new token to both).
            if (!await sessions.TryRotateAsync(session, presented, newHash, now, ct))
            {
                session.Revoke("refresh_reuse", now);
                await sessions.SaveChangesAsync(ct);
                await RaiseReuseAsync(session, cmd.Ip, ct);
                return Session.RefreshReused;
            }
            await sessions.SaveChangesAsync(ct);
        }
        else
        {
            var rotated = session.Rotate(presented, newHash, now);
            await sessions.SaveChangesAsync(ct);   // ALWAYS save: on reuse, Rotate just revoked the session and that must stick
            if (rotated.IsFailure)
            {
                if (rotated.Error == Session.RefreshReused) await RaiseReuseAsync(session, cmd.Ip, ct);
                return rotated.Error;
            }
        }

        var accessToken = tokens.IssueAccessToken(new AccessTokenClaims(
            user.Id, user.TenantId, user.Role.ToString(),
            SessionId: session.Id,
            Amr: session.IsDemo ? [DemoSignInHandler.Method] : ["pwd", "mfa"],   // how the user originally signed in (RFC 8176 values)
            AuthTime: session.CreatedAt,  // when they actually signed in, not now
            TokenVersion: user.TokenVersion), now);

        return new RefreshResult(accessToken, newToken, session.ExpiresAt);
    }

    // A refresh token that was already used came back: someone holds a copy. The session is already ended; tell the security page.
    private async Task RaiseReuseAsync(Session session, string? ip, CancellationToken ct)
    {
        var raised = SecurityEvent.Create(session.TenantId, clock.UtcNow, Severity.High, "refresh.reuse", userId: session.UserId, ip: ip);
        if (raised.IsSuccess) await securityEvents.WriteAsync(raised.Value, ct);
        else logger.LogError("Could not build security event refresh.reuse: {Code}", raised.Error.Code);
    }
}
