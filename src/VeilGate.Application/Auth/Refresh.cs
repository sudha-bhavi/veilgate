using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.Domain.Common;

namespace VeilGate.Application.Auth;

public sealed record RefreshCommand(string? RefreshToken);

public sealed record RefreshResult(string AccessToken, string RefreshToken, DateTimeOffset RefreshExpiresAt);

/// <summary>Swaps a refresh token for a new access token AND a new refresh token (rotation). A replayed old token ends the session.</summary>
public sealed class RefreshHandler(
    ISessionRepository sessions, IUserRepository users, ITenantContext tenant, ITokenService tokens, IClock clock)
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
        var rotated = session.Rotate(presented, RefreshTokens.Hash(newToken), now);
        await sessions.SaveChangesAsync(ct);   // ALWAYS save: on reuse, Rotate just revoked the session and that must stick
        if (rotated.IsFailure) return rotated.Error;

        var accessToken = tokens.IssueAccessToken(new AccessTokenClaims(
            user.Id, user.TenantId, user.Role.ToString(),
            SessionId: session.Id,
            Amr: ["pwd", "mfa"],          // how the user originally signed in (RFC 8176 values)
            AuthTime: session.CreatedAt,  // when they actually signed in, not now
            TokenVersion: user.TokenVersion), now);

        return new RefreshResult(accessToken, newToken, session.ExpiresAt);
    }
}