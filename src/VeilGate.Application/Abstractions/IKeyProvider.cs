namespace VeilGate.Application.Abstractions;

/// <summary>A usable (unwrapped) data key and its version number.</summary>
public sealed record DataKey(int Version, byte[] Key);

/// <summary>Hands out a tenant's data keys. Application knows the promise; Infrastructure decides where the master key lives.</summary>
public interface IKeyProvider
{
    /// <summary>The key to encrypt with now. Creates the tenant's first key if there is none.</summary>
    Task<DataKey> GetActiveKeyAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>A specific older or current version, for decrypting.</summary>
    Task<DataKey> GetKeyAsync(Guid tenantId, int version, CancellationToken ct = default);
}
