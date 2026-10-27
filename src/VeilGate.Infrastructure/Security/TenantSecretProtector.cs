using System.Security.Cryptography;
using System.Text;
using VeilGate.Application.Abstractions;

namespace VeilGate.Infrastructure.Security;

/// <summary>
/// Envelope encryption for small per-tenant secrets such as a provider's API key (Database doc 6.4): AES-256-GCM under the tenant's
/// data key, a fresh random 96-bit nonce every time, and a context string bound in as associated data. The stored value is
/// nonce, then tag, then ciphertext. The version of the data key is kept next to it by the caller, so keys can rotate.
/// </summary>
public sealed class TenantSecretProtector(IKeyProvider keys) : ITenantSecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public async Task<ProtectedSecret> ProtectAsync(Guid tenantId, string plaintext, string context, CancellationToken ct = default)
    {
        var dek = await keys.GetActiveKeyAsync(tenantId, ct);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(dek.Key, TagSize))
            aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(context));
        CryptographicOperations.ZeroMemory(plain);

        var blob = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, NonceSize);
        cipher.CopyTo(blob, NonceSize + TagSize);
        return new ProtectedSecret(blob, dek.Version);
    }

    public async Task<string> UnprotectAsync(Guid tenantId, byte[] cipher, int dekVersion, string context, CancellationToken ct = default)
    {
        if (cipher.Length < NonceSize + TagSize) throw new CryptographicException("Protected value is too short.");
        var dek = await keys.GetKeyAsync(tenantId, dekVersion, ct);

        var plain = new byte[cipher.Length - NonceSize - TagSize];
        using (var aes = new AesGcm(dek.Key, TagSize))
            // Throws if the key, the context, or even one bit of the data is wrong.
            aes.Decrypt(cipher.AsSpan(0, NonceSize), cipher.AsSpan(NonceSize + TagSize), cipher.AsSpan(NonceSize, TagSize), plain, Encoding.UTF8.GetBytes(context));

        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
