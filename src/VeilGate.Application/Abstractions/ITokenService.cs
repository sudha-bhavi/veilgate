namespace VeilGate.Application.Abstractions;

public sealed record AccessTokenClaims(Guid UserId, Guid TenantId, string Role, Guid SessionId, string[] Amr, DateTimeOffset AuthTime, int TokenVersion);

/// <summary>
/// An API client's token: who the client is, which tenant it belongs to, and what it may call (e.g. gateway:invoke).
/// <paramref name="RateLimitPerMinute"/> is the client's own limit, carried in the token so the rate limiter needs no database read;
/// null means "use the gateway-wide default".
/// </summary>
public sealed record ClientTokenClaims(Guid ClientId, Guid TenantId, string[] Scopes, int? RateLimitPerMinute = null);

public static class TokenLifetimes
{
    /// <summary>How long a client token lives (Backend doc 3.2: expires_in 900). A revoked client is also refused on every call, so this is only the outer limit.</summary>
    public const int ClientTokenSeconds = 900;
}

public interface ITokenService
{
    /// <summary>15-minute ES256 token for an API client (OAuth 2.0 client credentials). It carries client_id and scope and no user, role or session.</summary>
    string IssueClientToken(ClientTokenClaims claims, DateTimeOffset? now = null);

    /// <summary>10-minute ES256 access JWT (typ at+jwt). The optional "now" exists for tests.</summary>
    string IssueAccessToken(AccessTokenClaims claims, DateTimeOffset? now = null);

    /// <summary>Short-lived token proving the password step is done. It cannot be used as an access token (different typ and audience).</summary>
    string IssueMfaChallengeToken(Guid userId, Guid tenantId, int tokenVersion, DateTimeOffset? now = null);
}
