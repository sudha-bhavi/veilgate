namespace VeilGate.Domain.Vault;

/// <summary>One protected value in the vault. The original exists only inside Ciphertext (AES-256-GCM).</summary>
public sealed class VaultToken
{
    public long Id { get; private set; }   // assigned by the database
    public Guid TenantId { get; init; }
    public Guid SessionId { get; init; }
    public string Token { get; init; } = string.Empty;
    public string EntityType { get; init; } = string.Empty;
    public byte[] Ciphertext { get; init; } = [];
    public byte[] Nonce { get; init; } = [];     // 12 bytes, new for every value
    public byte[] Tag { get; init; } = [];       // 16 bytes, the tamper check
    public int DekVersion { get; init; }         // which key encrypted it, so keys can rotate
}
