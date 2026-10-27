using System.Globalization;
using System.Net;
using System.Net.Sockets;
using VeilGate.Domain.Common;

namespace VeilGate.Domain.ApiClients;

public enum ApiClientStatus { Active, Revoked }

/// <summary>
/// An application that calls the gateway with OAuth 2.0 client credentials (Backend doc 5.1, Database doc 2.5).
/// It belongs to one tenant, may use only the scopes it was given, and may be limited to certain networks.
/// The secret is not part of this class: only its hash is stored, in <see cref="ApiClientSecret"/>.
/// </summary>
public sealed class ApiClient : TenantEntity
{
    public static readonly IReadOnlyList<string> AllowedScopes =
        ["gateway:invoke", "gateway:detect", "gateway:deidentify", "gateway:reidentify"];

    public const int DefaultRateLimitPerMinute = 600;
    public const int MaxRateLimitPerMinute = 10_000;
    public const int MaxIpAllowlistEntries = 50;

    private ApiClient() { }

    /// <summary>The public identifier clients send to /oauth/token (vg_ plus random hex). Not the database id.</summary>
    public string ClientId { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string[] Scopes { get; private set; } = [];
    public Guid? DefaultPolicyId { get; private set; }
    public string[] IpAllowlist { get; private set; } = [];
    public int RateLimitPerMinute { get; private set; } = DefaultRateLimitPerMinute;
    public Guid OwnerUserId { get; private set; }
    public ApiClientStatus Status { get; private set; } = ApiClientStatus.Active;
    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsActive => Status == ApiClientStatus.Active;

    public static Result<ApiClient> Create(
        Guid tenantId, Guid ownerUserId, string name, IEnumerable<string>? scopes,
        Guid? defaultPolicyId = null, IEnumerable<string>? ipAllowlist = null, int? rateLimitPerMinute = null)
    {
        if (tenantId == Guid.Empty || ownerUserId == Guid.Empty)
            return Error.Validation("validation.failed", "Tenant and owner are required.");

        var cleanName = (name ?? string.Empty).Trim();
        if (cleanName.Length is < 1 or > 80) return Error.Validation("validation.failed", "Name must be 1 to 80 characters.");

        var cleanScopes = NormalizeScopes(scopes);
        if (cleanScopes.IsFailure) return cleanScopes.Error;
        var cleanIps = NormalizeIpAllowlist(ipAllowlist);
        if (cleanIps.IsFailure) return cleanIps.Error;
        var limit = rateLimitPerMinute ?? DefaultRateLimitPerMinute;
        if (limit is < 1 or > MaxRateLimitPerMinute)
            return Error.Validation("validation.failed", $"Rate limit must be 1 to {MaxRateLimitPerMinute} per minute.");

        var client = new ApiClient
        {
            ClientId = ApiClientCredentials.NewClientId(),
            Name = cleanName,
            Scopes = cleanScopes.Value,
            DefaultPolicyId = defaultPolicyId,
            IpAllowlist = cleanIps.Value,
            RateLimitPerMinute = limit,
            OwnerUserId = ownerUserId
        };
        client.SetTenant(tenantId);
        return client;
    }

    /// <summary>Changes only what is given; a null argument means "leave as it is". All changes are checked before any is applied.</summary>
    public Result Update(IEnumerable<string>? scopes, IEnumerable<string>? ipAllowlist, int? rateLimitPerMinute, Guid? defaultPolicyId)
    {
        if (!IsActive) return Result.Failure(Error.Conflict("resource.conflict", "A revoked client cannot be changed."));

        string[]? newScopes = null, newIps = null;
        if (scopes is not null)
        {
            var r = NormalizeScopes(scopes);
            if (r.IsFailure) return Result.Failure(r.Error);
            newScopes = r.Value;
        }
        if (ipAllowlist is not null)
        {
            var r = NormalizeIpAllowlist(ipAllowlist);
            if (r.IsFailure) return Result.Failure(r.Error);
            newIps = r.Value;
        }
        if (rateLimitPerMinute is < 1 or > MaxRateLimitPerMinute)
            return Result.Failure(Error.Validation("validation.failed", $"Rate limit must be 1 to {MaxRateLimitPerMinute} per minute."));

        if (newScopes is not null) Scopes = newScopes;
        if (newIps is not null) IpAllowlist = newIps;
        if (rateLimitPerMinute is { } limit) RateLimitPerMinute = limit;
        if (defaultPolicyId is { } policy) DefaultPolicyId = policy;
        return Result.Success();
    }

    public Result Revoke(DateTimeOffset now)
    {
        if (!IsActive) return Result.Failure(Error.Conflict("resource.conflict", "Client is already revoked."));
        Status = ApiClientStatus.Revoked;
        RevokedAt = now;
        return Result.Success();
    }

    /// <summary>An empty allowlist means "from anywhere". Otherwise the caller's address must be inside one of the networks.</summary>
    public bool IsIpAllowed(string? ip) => IsIpAllowedBy(IpAllowlist, ip);

    /// <summary>The same rule for code that holds only the list (the gateway checks it on every call without loading the whole client).</summary>
    public static bool IsIpAllowedBy(IReadOnlyCollection<string> allowlist, string? ip)
    {
        if (allowlist.Count == 0) return true;
        if (!IPAddress.TryParse(ip, out var address)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();   // ::ffff:203.0.113.9 is the same caller as 203.0.113.9
        return allowlist.Any(entry => IPNetwork.TryParse(entry, out var net) && net.Contains(address));
    }

    /// <summary>
    /// Accepts "203.0.113.0/24", "2001:db8::/32" or a single address, and returns the one canonical spelling.
    /// A network with bits set after the prefix (203.0.113.9/24) is refused rather than silently changed.
    /// </summary>
    public static bool TryNormalizeCidr(string? value, out string normalized)
    {
        normalized = string.Empty;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 64) return false;

        if (!text.Contains('/'))
        {
            if (!IPAddress.TryParse(text, out var single)) return false;
            if (single.IsIPv4MappedToIPv6) single = single.MapToIPv4();
            text = $"{single}/{(single.AddressFamily == AddressFamily.InterNetwork ? 32 : 128)}";
        }

        var slash = text.IndexOf('/');
        if (!IPAddress.TryParse(text[..slash], out var address)) return false;
        if (!int.TryParse(text[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix)) return false;

        var bytes = address.GetAddressBytes();
        var totalBits = bytes.Length * 8;
        if (prefix > totalBits) return false;
        for (var bit = prefix; bit < totalBits; bit++)   // every bit after the prefix must be zero
            if ((bytes[bit / 8] & (0x80 >> (bit % 8))) != 0) return false;

        normalized = $"{address}/{prefix}";
        return true;
    }

    private static Result<string[]> NormalizeScopes(IEnumerable<string>? scopes)
    {
        var list = (scopes ?? []).Select(s => s?.Trim() ?? string.Empty).Distinct(StringComparer.Ordinal).ToArray();
        if (list.Length == 0) return Error.Validation("validation.failed", "At least one scope is required.");
        var unknown = list.FirstOrDefault(s => !AllowedScopes.Contains(s, StringComparer.Ordinal));
        if (unknown is not null) return Error.Validation("validation.failed", "Unknown scope.");
        return list;
    }

    private static Result<string[]> NormalizeIpAllowlist(IEnumerable<string>? entries)
    {
        var result = new List<string>();
        foreach (var entry in entries ?? [])
        {
            if (!TryNormalizeCidr(entry, out var normalized)) return Error.Validation("validation.failed", "Invalid CIDR.");
            if (!result.Contains(normalized)) result.Add(normalized);
        }
        if (result.Count > MaxIpAllowlistEntries)
            return Error.Validation("validation.failed", $"At most {MaxIpAllowlistEntries} networks.");
        return result.ToArray();
    }
}
