using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using VeilGate.Api.Options;

namespace VeilGate.Api.Security;

/// <summary>One public key as published in the JWKS. Contains no private parameters by construction.</summary>
public sealed record PublicJwk(string Kty, string Crv, string X, string Y, string Kid, string Use, string Alg);

public interface ISigningKeyProvider
{
    SigningCredentials Active { get; }
    IReadOnlyList<SecurityKey> ValidationKeys { get; }
    IReadOnlyList<PublicJwk> PublicJwks { get; }
}

public sealed class SigningKeyProvider : ISigningKeyProvider, IDisposable
{
    private const string P256Oid = "1.2.840.10045.3.1.7";
    private readonly List<ECDsa> _owned = new();
    private readonly List<ECDsaSecurityKey> _validation = new();
    private readonly List<PublicJwk> _jwks = new();

    public SigningCredentials Active { get; }
    public IReadOnlyList<SecurityKey> ValidationKeys => _validation;
    public IReadOnlyList<PublicJwk> PublicJwks => _jwks;

    public SigningKeyProvider(IOptions<AuthOptions> options, IHostEnvironment env, ILogger<SigningKeyProvider> log)
    {
        ECDsaSecurityKey? signing = null;

        foreach (var k in options.Value.Keys.Where(k => !string.IsNullOrWhiteSpace(k.Kid)))
        {
            var pem = !string.IsNullOrWhiteSpace(k.PrivateKeyPem) ? k.PrivateKeyPem : k.PublicKeyPem;
            if (string.IsNullOrWhiteSpace(pem)) throw new InvalidOperationException($"Auth key '{k.Kid}' has neither PrivateKeyPem nor PublicKeyPem.");

            var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(pem.Replace("\\n", "\n")); // allow keys passed through environment variables with escaped newlines
            _owned.Add(ecdsa);
            var key = Register(ecdsa, k.Kid);
            if (signing is null && HasPrivateKey(ecdsa)) signing = key;
        }

        if (signing is null)
        {
            if (!env.IsDevelopment())
                throw new InvalidOperationException("Auth:Keys needs at least one key with a PrivateKeyPem (ES256 / P-256).");
            var dev = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _owned.Add(dev);
            signing = Register(dev, "dev-" + Guid.NewGuid().ToString("N")[..8]);
            log.LogWarning("No signing key configured. Using a temporary development key; tokens stop working when the app restarts.");
        }

        Active = new SigningCredentials(signing, SecurityAlgorithms.EcdsaSha256);
    }

    private ECDsaSecurityKey Register(ECDsa ecdsa, string kid)
    {
        var p = ecdsa.ExportParameters(false);
        if (p.Curve.Oid?.Value != P256Oid && p.Curve.Oid?.FriendlyName is not ("nistP256" or "ECDSA_P256"))
            throw new InvalidOperationException($"Auth key '{kid}' must be on curve P-256 (ES256).");

        var key = new ECDsaSecurityKey(ecdsa) { KeyId = kid };
        _validation.Add(key);
        _jwks.Add(new PublicJwk("EC", "P-256", Base64UrlEncoder.Encode(p.Q.X!), Base64UrlEncoder.Encode(p.Q.Y!), kid, "sig", "ES256"));
        return key;
    }

    private static bool HasPrivateKey(ECDsa ecdsa)
    {
        try { return ecdsa.ExportParameters(true).D is { Length: > 0 }; }
        catch (CryptographicException) { return false; }
    }

    public void Dispose() { foreach (var e in _owned) e.Dispose(); }
}
