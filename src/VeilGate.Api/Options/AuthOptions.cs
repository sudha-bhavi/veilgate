using System.ComponentModel.DataAnnotations;

namespace VeilGate.Api.Options;

public sealed class AuthOptions
{
    public const string Section = "Auth";

    [Required] public string Issuer { get; set; } = "https://veilgate.local";
    [Required] public string Audience { get; set; } = "veilgate-api";
    [Range(1, 60)] public int AccessTokenMinutes { get; set; } = 10;
    [Range(1, 30)] public int MfaChallengeMinutes { get; set; } = 5;
    [Range(1, 100_000)] public int LoginRateLimitPerMinute { get; set; } = 10;

    /// <summary>
    /// ES256 (P-256) keys. The first key that has a PrivateKeyPem signs new tokens; every key's public half is published at
    /// /.well-known/jwks.json and accepted for verification. To rotate: add the new key, deploy, then retire the old key after 10 minutes.
    /// </summary>
    public List<SigningKeyOptions> Keys { get; set; } = new();
}

public sealed class SigningKeyOptions
{
    public string Kid { get; set; } = string.Empty;
    public string? PrivateKeyPem { get; set; }
    public string? PublicKeyPem { get; set; }
}
