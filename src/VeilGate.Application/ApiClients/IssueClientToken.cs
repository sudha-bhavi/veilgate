using VeilGate.Application.Abstractions;
using VeilGate.Domain.Audit;
using VeilGate.Domain.Common;

namespace VeilGate.Application.ApiClients;

public sealed record IssueClientTokenCommand(string? ClientId, string? ClientSecret, string? Scope, string? Ip);
public sealed record IssuedClientToken(string AccessToken, int ExpiresInSeconds, string Scope);

/// <summary>
/// OAuth 2.0 client credentials (Backend doc 3.2, POST /oauth/token). Checks the client id and secret and issues a short token
/// carrying only the scopes that were asked for and the client is allowed. Like login, every failure that could reveal whether a
/// client exists looks the same and takes about the same time.
/// </summary>
public sealed class IssueClientTokenHandler(
    IApiClientRepository clients, IUserRepository users, IPasswordHasher hasher, ITenantContext tenant,
    ITokenService tokens, ISecurityEventWriter securityEvents, IClock clock)
{
    private const int MaxClientIdLength = 40;     // the column size; anything longer cannot be ours
    private const int MaxSecretLength = 256;      // stops giant inputs from burning hashing time

    public static readonly Error InvalidClient = Error.Unauthorized("oauth.invalid_client", "Client authentication failed");
    public static readonly Error InvalidScope = Error.Validation("oauth.invalid_scope", "The requested scope is not allowed for this client");

    public async Task<Result<IssuedClientToken>> HandleAsync(IssueClientTokenCommand cmd, CancellationToken ct = default)
    {
        var secret = cmd.ClientSecret ?? string.Empty;
        if (string.IsNullOrWhiteSpace(cmd.ClientId) || cmd.ClientId.Length > MaxClientIdLength
            || secret.Length is 0 or > MaxSecretLength)
        {
            hasher.VerifyDummy(secret.Length > MaxSecretLength ? string.Empty : secret);
            return InvalidClient;
        }

        // The lookup that runs before the tenant is known. Only an ACTIVE client is found, so "revoked" looks like "unknown".
        var tenantId = await clients.FindTenantByClientIdAsync(cmd.ClientId, ct);
        if (tenantId is null)
        {
            hasher.VerifyDummy(secret);
            return InvalidClient;
        }

        // From here on the tenant is known, so row-level security applies to everything we read or write.
        tenant.Set(tenantId.Value);
        var client = await clients.FindByClientIdAsync(cmd.ClientId, ct);
        if (client is null || !client.IsActive || !await users.IsTenantActiveAsync(ct))
        {
            hasher.VerifyDummy(secret);
            return InvalidClient;
        }

        var now = clock.UtcNow;
        var valid = (await clients.ListSecretsAsync(client.Id, ct)).Where(s => s.IsValidAt(now)).ToList();
        var matched = false;
        foreach (var candidate in valid) matched |= hasher.Verify(secret, candidate.SecretHash);   // no early exit: at most two checks either way
        if (valid.Count == 0) hasher.VerifyDummy(secret);

        if (!matched)
        {
            await RecordAsync("oauth.invalid_secret", Severity.Low, client.Id, tenantId.Value, cmd.Ip, now, ct);
            return InvalidClient;
        }

        if (!client.IsIpAllowed(cmd.Ip))
        {
            // Same answer as a wrong secret, so a stolen secret does not tell the thief which networks are allowed.
            await RecordAsync("oauth.ip_denied", Severity.Medium, client.Id, tenantId.Value, cmd.Ip, now, ct);
            return InvalidClient;
        }

        var requested = (cmd.Scope ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
        var granted = requested.Length == 0 ? client.Scopes : requested;
        if (granted.Any(s => !client.Scopes.Contains(s, StringComparer.Ordinal))) return InvalidScope;

        var token = tokens.IssueClientToken(new ClientTokenClaims(client.Id, tenantId.Value, granted, client.RateLimitPerMinute), now);
        return new IssuedClientToken(token, TokenLifetimes.ClientTokenSeconds, string.Join(' ', granted));
    }

    private async Task RecordAsync(string type, Severity severity, Guid clientId, Guid tenantId, string? ip, DateTimeOffset now, CancellationToken ct)
    {
        var created = SecurityEvent.Create(tenantId, now, severity, type, clientId: clientId, ip: ip);
        if (created.IsSuccess) await securityEvents.WriteAsync(created.Value, ct);
    }
}
