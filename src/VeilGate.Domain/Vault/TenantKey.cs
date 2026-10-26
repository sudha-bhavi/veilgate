namespace VeilGate.Domain.Vault;

/// <summary>A tenant's data encryption key, stored wrapped by the master key. Never the raw key.</summary>
public sealed class TenantKey
{
    public Guid TenantId { get; init; }
    public int DekVersion { get; init; }
    public byte[] WrappedDek { get; init; } = [];
    public string Status { get; init; } = "Active";   // Active or Retired
    public DateTimeOffset CreatedAt { get; init; }
}
