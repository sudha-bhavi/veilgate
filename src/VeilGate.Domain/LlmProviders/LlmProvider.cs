using System.Text.RegularExpressions;
using VeilGate.Domain.Common;

namespace VeilGate.Domain.LlmProviders;

/// <summary>An API key as it is stored: encrypted under the tenant's data key (DekVersion says which), plus its last four characters for display.</summary>
public sealed record ProtectedApiKey(byte[] Cipher, int DekVersion, string Last4);

/// <summary>
/// A language model service a tenant has connected (Backend doc 5.1, Database doc 3.2): where it is, which models it serves,
/// how long to wait for it, and where it sits in the failover order. The API key is held only as ciphertext.
/// Whether the address is safe to call (not an internal network) depends on the environment, so the application layer checks that.
/// </summary>
public sealed class LlmProvider : TenantEntity
{
    public const int MaxNameLength = 80;
    public const int MaxBaseUrlLength = 300;
    public const int MaxModels = 50;
    public const int DefaultTimeoutSeconds = 60;
    public const int MaxTimeoutSeconds = 300;

    private static readonly Regex ModelPattern = new("^[A-Za-z0-9][A-Za-z0-9._:/@+-]{0,79}$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private LlmProvider() { }

    public string Name { get; private set; } = string.Empty;
    public string BaseUrl { get; private set; } = string.Empty;
    public byte[] ApiKeyCipher { get; private set; } = [];
    public int DekVersion { get; private set; }
    public string KeyLast4 { get; private set; } = string.Empty;
    public string[] Models { get; private set; } = [];
    public int TimeoutSeconds { get; private set; } = DefaultTimeoutSeconds;

    /// <summary>Lower is tried first. Failover goes to the next one up.</summary>
    public int Priority { get; private set; }
    public bool Enabled { get; private set; } = true;

    /// <param name="id">Chosen by the caller because the encrypted key is bound to it, so it has to be known before the key is encrypted.</param>
    public static Result<LlmProvider> Create(
        Guid id, Guid tenantId, string name, string baseUrl, IEnumerable<string>? models, ProtectedApiKey key,
        int? timeoutSeconds = null, int priority = 0, bool enabled = true)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty) return Error.Validation("validation.failed", "Id and tenant are required.");
        var check = Validate(name, baseUrl, models, timeoutSeconds ?? DefaultTimeoutSeconds);
        if (check.IsFailure) return check.Error;
        var keyCheck = CheckKey(key);
        if (keyCheck.IsFailure) return keyCheck.Error;

        var provider = new LlmProvider
        {
            Id = id,
            Name = check.Value.Name,
            BaseUrl = check.Value.BaseUrl,
            Models = check.Value.Models,
            TimeoutSeconds = timeoutSeconds ?? DefaultTimeoutSeconds,
            Priority = priority,
            Enabled = enabled,
            ApiKeyCipher = key.Cipher,
            DekVersion = key.DekVersion,
            KeyLast4 = key.Last4
        };
        provider.SetTenant(tenantId);
        return provider;
    }

    /// <summary>Name, address and models are always given; the rest stay as they are when null. The key changes only through <see cref="ReplaceApiKey"/>.</summary>
    public Result Update(string name, string baseUrl, IEnumerable<string>? models, int? timeoutSeconds, bool? enabled, int? priority)
    {
        var check = Validate(name, baseUrl, models, timeoutSeconds ?? TimeoutSeconds);
        if (check.IsFailure) return Result.Failure(check.Error);

        Name = check.Value.Name;
        BaseUrl = check.Value.BaseUrl;
        Models = check.Value.Models;
        if (timeoutSeconds is { } t) TimeoutSeconds = t;
        if (enabled is { } e) Enabled = e;
        if (priority is { } p) Priority = p;
        return Result.Success();
    }

    public Result ReplaceApiKey(ProtectedApiKey key)
    {
        var check = CheckKey(key);
        if (check.IsFailure) return check;
        ApiKeyCipher = key.Cipher;
        DekVersion = key.DekVersion;
        KeyLast4 = key.Last4;
        return Result.Success();
    }

    public void SetPriority(int priority) => Priority = priority;

    public bool Serves(string model) => Models.Contains(model, StringComparer.Ordinal);

    public static bool IsValidModelName(string? model) => model is not null && ModelPattern.IsMatch(model.Trim());

    /// <summary>The last four characters of a key (padded if it is shorter), the only part of it that is ever shown.</summary>
    public static string Last4Of(string apiKey) => apiKey.Length >= 4 ? apiKey[^4..] : apiKey.PadLeft(4, '*');

    /// <summary>
    /// One canonical spelling of the address: lower-case scheme and host, no trailing slash. Only http(s) with a host, and nothing that
    /// could smuggle a credential or a different destination (user info, query, fragment).
    /// </summary>
    public static bool TryNormalizeBaseUrl(string? value, out string normalized)
    {
        normalized = string.Empty;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxBaseUrlLength) return false;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return false;
        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;

        var result = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        if (result.Length > MaxBaseUrlLength) return false;
        normalized = result;
        return true;
    }

    private sealed record Checked(string Name, string BaseUrl, string[] Models);

    private static Result<Checked> Validate(string name, string baseUrl, IEnumerable<string>? models, int timeoutSeconds)
    {
        var cleanName = (name ?? string.Empty).Trim();
        if (cleanName.Length is < 1 or > MaxNameLength) return Error.Validation("validation.failed", $"Name must be 1 to {MaxNameLength} characters.");
        if (!TryNormalizeBaseUrl(baseUrl, out var url)) return Error.Validation("validation.failed", "Invalid provider URL.");
        if (timeoutSeconds is < 1 or > MaxTimeoutSeconds)
            return Error.Validation("validation.failed", $"Timeout must be 1 to {MaxTimeoutSeconds} seconds.");

        var list = (models ?? []).Select(m => m?.Trim() ?? string.Empty).Distinct(StringComparer.Ordinal).ToArray();
        if (list.Length == 0) return Error.Validation("validation.failed", "At least one model is required.");
        if (list.Length > MaxModels) return Error.Validation("validation.failed", $"At most {MaxModels} models.");
        if (list.Any(m => !ModelPattern.IsMatch(m))) return Error.Validation("validation.failed", "Invalid model name.");

        return new Checked(cleanName, url, list);
    }

    private static Result CheckKey(ProtectedApiKey key) =>
        key.Cipher.Length == 0 || key.DekVersion < 1 || key.Last4.Length != 4
            ? Result.Failure(Error.Validation("validation.failed", "API key is required."))
            : Result.Success();
}
