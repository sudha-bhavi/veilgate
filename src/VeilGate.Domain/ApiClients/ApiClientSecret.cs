using System.Security.Cryptography;
using VeilGate.Domain.Common;

namespace VeilGate.Domain.ApiClients;

public static class ApiClientCredentials
{
    public const string ClientIdPrefix = "vg_";
    public const string SecretPrefix = "vgs_";

    /// <summary>vg_ plus 32 random hex characters (35 in all; the column holds 40).</summary>
    public static string NewClientId() =>
        ClientIdPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>vgs_ plus 256 random bits, URL-safe. Shown to the person once; only its Argon2id hash is kept.</summary>
    public static string NewSecret() =>
        SecretPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// One secret of a client, stored only as a hash. During a rotation a client has two: the new one, and the old one
/// until its grace period ends (Database doc 2.5).
/// </summary>
public sealed class ApiClientSecret : TenantEntity
{
    private ApiClientSecret() { }

    public Guid ClientPk { get; private set; }
    public string SecretHash { get; private set; } = string.Empty;

    /// <summary>Null means "no end date".</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    public static Result<ApiClientSecret> Create(Guid tenantId, Guid clientPk, string secretHash)
    {
        if (tenantId == Guid.Empty || clientPk == Guid.Empty) return Error.Validation("validation.failed", "Tenant and client are required.");
        if (string.IsNullOrWhiteSpace(secretHash)) return Error.Validation("validation.failed", "Secret hash is required.");
        var secret = new ApiClientSecret { ClientPk = clientPk, SecretHash = secretHash };
        secret.SetTenant(tenantId);
        return secret;
    }

    public bool IsValidAt(DateTimeOffset now) => ExpiresAt is null || now < ExpiresAt;

    /// <summary>Makes sure the secret stops working no later than <paramref name="when"/>. It never extends an earlier end date.</summary>
    public void ExpireNoLaterThan(DateTimeOffset when)
    {
        if (ExpiresAt is null || ExpiresAt > when) ExpiresAt = when;
    }
}
