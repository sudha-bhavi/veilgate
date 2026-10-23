using System.Buffers.Binary;
using System.Security.Cryptography;

namespace VeilGate.Domain;

public static class Totp
{
    public const int StepSeconds = 30;

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
}