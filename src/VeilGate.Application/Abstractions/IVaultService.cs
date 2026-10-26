using VeilGate.Domain.Vault;

namespace VeilGate.Application.Abstractions;

public interface IVaultService
{
    /// <summary>Starts a vault session. The result carries the date-shift key; nothing is saved until StoreAsync.</summary>
    Task<VaultSession> OpenAsync(Guid tenantId, Guid clientId, TimeSpan ttl, string? subjectId = null, CancellationToken ct = default);

    /// <summary>Saves the session and encrypts every token it handed out. Safe to call again; already-saved tokens are skipped.</summary>
    Task StoreAsync(VaultSession session, CancellationToken ct = default);

    /// <summary>Finds a live session. Returns null if it is missing, expired, another tenant's, or another client's (all look the same).</summary>
    Task<VaultSession?> FindAsync(Guid sessionId, Guid clientId, CancellationToken ct = default);

    /// <summary>Decrypts one token. Returns null if unknown or the session has expired.</summary>
    Task<string?> ResolveAsync(VaultSession session, string token, CancellationToken ct = default);

    /// <summary>Deletes expired sessions of the current tenant (their tokens go with them). Returns how many sessions were removed.</summary>
    Task<int> PurgeExpiredAsync(CancellationToken ct = default);
}
