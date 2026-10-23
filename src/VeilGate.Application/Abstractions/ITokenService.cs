namespace VeilGate.Application.Abstractions;

public sealed record AccessTokenClaims(Guid UserId, Guid TenantId, string Role, Guid SessionId, string[] Amr, DateTimeOffset AuthTime, int TokenVersion);

public interface ITokenService
{
    /// <summary>10-minute ES256 access JWT (typ at+jwt). The optional "now" exists for tests.</summary>
    string IssueAccessToken(AccessTokenClaims claims, DateTimeOffset? now = null);

    /// <summary>Short-lived token proving the password step is done. It cannot be used as an access token (different typ and audience).</summary>
    string IssueMfaChallengeToken(Guid userId, Guid tenantId, int tokenVersion, DateTimeOffset? now = null);
}
