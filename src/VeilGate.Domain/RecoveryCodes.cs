using System.Security.Cryptography;
using System.Text;

namespace VeilGate.Domain;

public static class RecoveryCodes
{
    public const int Count = 10;

    // No I, O, 0, 1 so codes can't be misread when written on paper.
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    // Makes 10 codes like "K7M2P-9QX4H". Uses the cryptographic random generator,
    // not System.Random, because these codes are secrets.
    public static IReadOnlyList<string> Generate()
    {
        var codes = new List<string>(Count);
        while (codes.Count < Count)
        {
            var chars = new char[10];
            for (var i = 0; i < chars.Length; i++)
                chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];

            var code = new string(chars, 0, 5) + "-" + new string(chars, 5, 5);
            if (!codes.Contains(code)) codes.Add(code); // never hand out duplicates
        }
        return codes;
    }

    // Turns whatever the user typed into one standard form:
    // "  k7m2p-9qx4h " becomes "K7M2P9QX4H".
    public static string Normalize(string input)
    {
        var kept = input.Where(char.IsLetterOrDigit);
        return new string(kept.ToArray()).ToUpperInvariant();
    }

    // We store only this hash, never the code itself.
    // If the database leaks, the attacker gets hashes, not usable codes.
    public static string Hash(string code)
    {
        var bytes = Encoding.UTF8.GetBytes(Normalize(code));
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    // Checks what the user typed against the stored hash.
    public static bool Matches(string input, string storedHash)
    {
        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrEmpty(storedHash))
            return false;

        var actual = Encoding.UTF8.GetBytes(Hash(input));
        var expected = Encoding.UTF8.GetBytes(storedHash);

        // Fixed-time compare: takes the same time whether the first byte or the
        // last byte differs, so timing can't leak anything. Returns false (not an
        // exception) when the lengths differ, e.g. a garbage stored hash.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}