using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using VeilGate.Api.Options;
using VeilGate.Application.Abstractions;

namespace VeilGate.Api.Security;

public sealed class TokenService(ISigningKeyProvider keys, IOptions<AuthOptions> options, IClock clock) : ITokenService
{
    public const string AccessTokenType = "at+jwt";
    public const string MfaTokenType = "mfa+jwt";
        public const string MfaChallengeScheme = "MfaChallenge";
    public static string MfaAudience(AuthOptions o) => o.Audience + "-mfa";

    private readonly JsonWebTokenHandler _handler = new();

    public string IssueAccessToken(AccessTokenClaims c, DateTimeOffset? now = null)
    {
        var o = options.Value;
        var t = (now ?? clock.UtcNow).UtcDateTime;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = o.Issuer,
            Audience = o.Audience,
            IssuedAt = t,
            NotBefore = t,
            Expires = t.AddMinutes(o.AccessTokenMinutes),
            TokenType = AccessTokenType,
            SigningCredentials = keys.Active,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = c.UserId.ToString(),
                ["tid"] = c.TenantId.ToString(),
                ["role"] = c.Role,
                ["sid"] = c.SessionId.ToString(),
                ["amr"] = c.Amr,
                ["auth_time"] = c.AuthTime.ToUnixTimeSeconds(),
                ["jti"] = Guid.NewGuid().ToString("N"),
                ["ver"] = c.TokenVersion
            }
        };
        return _handler.CreateToken(descriptor);
    }

    public const int ClientTokenMinutes = TokenLifetimes.ClientTokenSeconds / 60;   // Backend doc 3.2: expires_in 900

    public string IssueClientToken(ClientTokenClaims c, DateTimeOffset? now = null)
    {
        var o = options.Value;
        var t = (now ?? clock.UtcNow).UtcDateTime;
        var claims = new Dictionary<string, object>
        {
            ["sub"] = c.ClientId.ToString(),
            ["client_id"] = c.ClientId.ToString(),
            ["tid"] = c.TenantId.ToString(),
            ["scope"] = string.Join(' ', c.Scopes),   // OAuth 2.0 writes scopes as one space-separated string
            ["jti"] = Guid.NewGuid().ToString("N")
        };
        if (c.RateLimitPerMinute is { } limit) claims["rlim"] = limit;   // the client's own limit, read by the gateway rate limiter

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = o.Issuer,
            Audience = o.Audience,
            IssuedAt = t,
            NotBefore = t,
            Expires = t.AddMinutes(ClientTokenMinutes),
            TokenType = AccessTokenType,
            SigningCredentials = keys.Active,
            Claims = claims
        };
        return _handler.CreateToken(descriptor);
    }

    public string IssueMfaChallengeToken(Guid userId, Guid tenantId, int tokenVersion, DateTimeOffset? now = null)
    {
        var o = options.Value;
        var t = (now ?? clock.UtcNow).UtcDateTime;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = o.Issuer,
            Audience = MfaAudience(o),
            IssuedAt = t,
            NotBefore = t,
            Expires = t.AddMinutes(o.MfaChallengeMinutes),
            TokenType = MfaTokenType,
            SigningCredentials = keys.Active,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = userId.ToString(),
                ["tid"] = tenantId.ToString(),
                ["ver"] = tokenVersion,
                ["purpose"] = "mfa",
                ["jti"] = Guid.NewGuid().ToString("N")
            }
        };
        return _handler.CreateToken(descriptor);
    }
}
