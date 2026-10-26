using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace VeilGate.Domain;

/// <summary>Makes and hashes refresh tokens. The browser keeps the token; the database keeps only its hash.</summary>
public static class RefreshTokens
{
    // 32 random bytes = 256 bits, written as 43 URL-safe characters. Nobody can guess one.
    public static string Generate() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    // SHA-256 is enough here (unlike passwords): the token is already random, so there is nothing to guess.
    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}