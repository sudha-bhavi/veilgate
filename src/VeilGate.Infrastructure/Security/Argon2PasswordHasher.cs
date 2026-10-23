using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using VeilGate.Application.Abstractions;

namespace VeilGate.Infrastructure.Security;

/// <summary>
/// Argon2id with the OWASP minimum profile (19 MiB memory, 2 passes, 1 lane). Output is the standard PHC string, so the
/// parameters travel with each hash and can be raised later without breaking old hashes (NeedsRehash tells you which).
/// Parameters read back from a stored hash are capped, so a tampered row cannot make the server allocate gigabytes.
/// </summary>
public sealed class Argon2PasswordHasher : IPasswordHasher
{
    private const int MemoryKib = 19_456, Iterations = 2, Parallelism = 1, SaltBytes = 16, HashBytes = 32;
    private const int MaxMemoryKib = 262_144, MaxIterations = 10, MaxParallelism = 8;

    private static readonly Lazy<string> DummyHash = new(() => new Argon2PasswordHasher().Hash("veilgate-timing-equalizer"));

    private readonly record struct Parsed(int M, int T, int P, byte[] Salt, byte[] Hash);

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Compute(password, salt, MemoryKib, Iterations, Parallelism, HashBytes);
        return $"$argon2id$v=19$m={MemoryKib},t={Iterations},p={Parallelism}${ToB64(salt)}${ToB64(hash)}";
    }

    public bool Verify(string password, string encodedHash)
    {
        if (string.IsNullOrEmpty(password)) return false; // an empty password can never match, and the Argon2 library refuses to hash one
        if (!TryParse(encodedHash, out var p)) return false;
        var actual = Compute(password, p.Salt, p.M, p.T, p.P, p.Hash.Length);
        return CryptographicOperations.FixedTimeEquals(actual, p.Hash);
    }

    public void VerifyDummy(string password) => Verify(password, DummyHash.Value);

    public bool NeedsRehash(string encodedHash)
        => !TryParse(encodedHash, out var p) || p.M < MemoryKib || p.T < Iterations || p.P != Parallelism;

    private static byte[] Compute(string password, byte[] salt, int memoryKib, int iterations, int parallelism, int length)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKib,
            Iterations = iterations,
            DegreeOfParallelism = parallelism
        };
        return argon.GetBytes(length);
    }

    private static bool TryParse(string? encoded, out Parsed parsed)
    {
        parsed = default;
        if (string.IsNullOrEmpty(encoded)) return false;
        var parts = encoded.Split('$');
        if (parts.Length != 6 || parts[0].Length != 0 || parts[1] != "argon2id" || parts[2] != "v=19") return false;

        var kv = parts[3].Split(',');
        if (kv.Length != 3 || !kv[0].StartsWith("m=") || !kv[1].StartsWith("t=") || !kv[2].StartsWith("p=")) return false;
        if (!int.TryParse(kv[0].AsSpan(2), out var m) || !int.TryParse(kv[1].AsSpan(2), out var t) || !int.TryParse(kv[2].AsSpan(2), out var par)) return false;
        if (m is < 8 or > MaxMemoryKib || t is < 1 or > MaxIterations || par is < 1 or > MaxParallelism) return false;

        try
        {
            var salt = FromB64(parts[4]);
            var hash = FromB64(parts[5]);
            if (salt.Length < 8 || hash.Length is < 16 or > 64) return false;
            parsed = new Parsed(m, t, par, salt, hash);
            return true;
        }
        catch (FormatException) { return false; }
    }

    private static string ToB64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static byte[] FromB64(string s) => Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
}
