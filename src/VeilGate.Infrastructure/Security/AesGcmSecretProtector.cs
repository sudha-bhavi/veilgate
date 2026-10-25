using System.Security.Cryptography;
using System.Text;
using VeilGate.Application.Abstractions;

namespace VeilGate.Infrastructure.Security;

public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const string Prefix = "v1:";   // lets us change the format or key later
    private const int NonceSize = 12;      // AES-GCM standard
    private const int TagSize = 16;        // the tamper-check size

    private readonly byte[] _key;

    public AesGcmSecretProtector(byte[] key)
    {
        if (key.Length != 32)
            throw new ArgumentException("Key must be exactly 32 bytes (256 bits).", nameof(key));
        _key = key;
    }

    public string Protect(byte[] plaintext, string context)
    {
        // A new random nonce every time. Reusing a nonce with the same key breaks AES-GCM.
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, cipher, tag, Encoding.UTF8.GetBytes(context));

        // Store everything needed to decrypt in one string: nonce + tag + ciphertext.
        var blob = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, NonceSize);
        cipher.CopyTo(blob, NonceSize + TagSize);

        return Prefix + Convert.ToBase64String(blob);
    }

    public byte[] Unprotect(string protectedValue, string context)
    {
        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
            throw new CryptographicException("Unknown protected-value format.");

        byte[] blob;
        try { blob = Convert.FromBase64String(protectedValue[Prefix.Length..]); }
        catch (FormatException) { throw new CryptographicException("Protected value is not valid."); }

        if (blob.Length < NonceSize + TagSize)
            throw new CryptographicException("Protected value is too short.");

        var nonce = blob[..NonceSize];
        var tag = blob[NonceSize..(NonceSize + TagSize)];
        var cipher = blob[(NonceSize + TagSize)..];
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(_key, TagSize);
        // Throws if the key, the context, or even one bit of the data is wrong.
        aes.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(context));
        return plain;
    }
}