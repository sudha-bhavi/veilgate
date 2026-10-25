using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace VeilGate.Domain;

public static class Totp
{
    public const int StepSeconds = 30;
     public const int SecretBytes = 20;   // 160 bits, the size the standard recommends

    // A fresh random secret for one user. Cryptographic random, never System.Random.
    public static byte[] GenerateSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    public static string Compute(byte[] secret, long unixSeconds)
    {
        // 1. Which 30-second "window" are we in? Both sides get the same number.
        long counter = unixSeconds / StepSeconds;

        // 2. Turn that number into 8 bytes (big-endian), as the standard requires.
        Span<byte> message = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(message, counter);

        // 3. Mix the secret and the counter with HMAC-SHA1 -> 20 bytes.
        byte[] hash = HMACSHA1.HashData(secret, message);

        // 4. "Dynamic truncation": the last 4 bits pick where to read 4 bytes from.
        int offset = hash[^1] & 0x0F;
        int binary =
            ((hash[offset] & 0x7F) << 24) |
            (hash[offset + 1] << 16) |
            (hash[offset + 2] << 8) |
            hash[offset + 3];

        // 5. Keep the last 6 digits, padded with zeros.
        return (binary % 1_000_000).ToString("D6");
    }

       // The text inside the QR code. Authenticator apps read this format.
    public static string BuildUri(string issuer, string account, byte[] secret)
    {
        var i = Uri.EscapeDataString(issuer);
        var a = Uri.EscapeDataString(account);
        return $"otpauth://totp/{i}:{a}?secret={Base32.Encode(secret)}&issuer={i}&algorithm=SHA1&digits=6&period={StepSeconds}";
    }

    // Returns the window number that matched, or null if the code is wrong.
    // lastUsedCounter = the newest window already accepted for this user (-1 if none),
    // so the same code can never be used twice.
    public static long? Verify(byte[] secret, string code, long unixSeconds, long lastUsedCounter = -1)
    {
        code = new string(code.Where(char.IsAsciiDigit).ToArray());   // "123 456" -> "123456"
        if (code.Length != 6) return null;

        var given = Encoding.ASCII.GetBytes(code);
        long current = unixSeconds / StepSeconds;
        long? match = null;

        for (long window = current - 1; window <= current + 1; window++)   // allow clock drift
        {
            if (window < 0 || window <= lastUsedCounter) continue;          // block replays

            var expected = Encoding.ASCII.GetBytes(Compute(secret, window * StepSeconds));
            if (CryptographicOperations.FixedTimeEquals(given, expected) && match is null)
                match = window;
        }
        return match;
    }
}
