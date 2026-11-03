using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.Domain.Common;

namespace VeilGate.Application.Auth;

/// <param name="DemoUserEmail">The one seeded demo user, from configuration. Never taken from the request.</param>
/// <param name="Ip">The caller's address, for the audit log only.</param>
public sealed record DemoSignInCommand(string DemoUserEmail, string? Ip = null);

public sealed record DemoSignInResult(string AccessToken, string RefreshToken, DateTimeOffset RefreshExpiresAt);

/// <summary>
/// The "Try the demo" button: signs a visitor in as one seeded demo user, with no password and no second factor.
/// That is a deliberate exception to the sign-in rules, so it is kept narrow. It only ever signs in the configured demo user,
/// it refuses a demo user who could manage people or the platform, the session is marked as a demo and lasts one hour,
/// and the token's sign-in method is "demo", which can never pass the recent second-factor check.
/// </summary>
public sealed class DemoSignInHandler(
    IUserRepository users, ISessionRepository sessions, ITenantContext tenant, ITokenService tokens, IClock clock, AuthAuditor auditor)
{
    /// <summary>The sign-in method written into the token (the "amr" claim) for a demo session.</summary>
    public const string Method = "demo";

    public static readonly Error Unavailable = Error.NotFound("demo.unavailable", "The demo is not available.");

    public async Task<Result<DemoSignInResult>> HandleAsync(DemoSignInCommand cmd, CancellationToken ct = default)
    {
        var candidate = await users.FindLoginCandidateAsync(cmd.DemoUserEmail.Trim().ToLowerInvariant(), ct);
        if (candidate is null) return Unavailable;

        tenant.Set(candidate.TenantId);
        var user = await users.GetCurrentTenantUserAsync(candidate.UserId, ct);
        if (user is null || user.Status != UserStatus.Active || !await users.IsTenantActiveAsync(ct)) return Unavailable;

        // A mistake in configuration must never hand a visitor the power to manage people or the platform.
        if (user.Role is UserRole.TenantAdmin or UserRole.PlatformAdmin) return Unavailable;

        var now = clock.UtcNow;
        var refreshToken = RefreshTokens.Generate();
        var session = Session.Start(user.TenantId, user.Id, RefreshTokens.Hash(refreshToken), now, demo: true).Value;
        sessions.Add(session);
        await sessions.SaveChangesAsync(ct);
        await auditor.SucceededAsync(user.TenantId, user.Id, AuthAuditor.DemoSignIn, cmd.Ip, Method, ct);

        var accessToken = tokens.IssueAccessToken(new AccessTokenClaims(
            user.Id, user.TenantId, user.Role.ToString(),
            SessionId: session.Id,
            Amr: [Method],
            AuthTime: now,
            TokenVersion: user.TokenVersion), now);

        return new DemoSignInResult(accessToken, refreshToken, session.ExpiresAt);
    }
}
