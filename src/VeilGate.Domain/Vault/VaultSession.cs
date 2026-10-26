using System.Security.Cryptography;
using VeilGate.Domain.Detection;

namespace VeilGate.Domain.Vault;

/// <summary>
/// One de-identification "conversation": the tokens handed out and the originals behind them.
/// Tokens are random, never derived from the value, and unique inside the session (Backend doc 6.2).
/// </summary>
public sealed class VaultSession
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";   // base32

    private readonly Dictionary<string, string> _tokenByValue = new();   // "TYPE\0value" -> token
    private readonly Dictionary<string, string> _valueByToken = new();   // token -> original value

    /// <summary>A random UUID (v4), not guessable, because whoever holds it can ask for restoration (Database doc 3.3).</summary>
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TenantId { get; init; }
    public Guid ClientId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>token -> original value. Read-only view for the code that stores or restores them.</summary>
    public IReadOnlyDictionary<string, string> Tokens => _valueByToken;

    /// <summary>
    /// Returns the token for this value, creating it if new. The same person mentioned twice in one text gets the same token,
    /// so the model still sees them as one person.
    /// </summary>
    public string TokenFor(string entityType, string originalValue)
    {
        var key = entityType + "\0" + originalValue;
        if (_tokenByValue.TryGetValue(key, out var existing)) return existing;

        string token;
        do { token = $"[[{EntityTypes.Label(entityType)}_{RandomSuffix()}]]"; }
        while (_valueByToken.ContainsKey(token));

        _tokenByValue[key] = token;
        _valueByToken[token] = originalValue;
        return token;
    }

    private static string RandomSuffix()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        var chars = new char[4];
        for (var i = 0; i < 4; i++) chars[i] = Alphabet[bytes[i] % 32];   // 256 is a multiple of 32, so no bias
        return new string(chars);
    }
}