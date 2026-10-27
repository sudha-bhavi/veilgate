using VeilGate.Domain.Common;
using VeilGate.Domain.LlmProviders;

namespace VeilGate.Application.Abstractions;

public interface ILlmProviderRepository
{
    void Add(LlmProvider provider);
    void Remove(LlmProvider provider);

    /// <summary>Tracked, so the caller can change it and save. Null if it does not exist in the current tenant.</summary>
    Task<LlmProvider?> FindAsync(Guid id, CancellationToken ct);

    /// <summary>All of the tenant's providers in failover order (priority, then name).</summary>
    Task<IReadOnlyList<LlmProvider>> ListAsync(CancellationToken ct);

    Task<bool> NameExistsAsync(string name, Guid? exceptId, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>A secret as it is stored: ciphertext and the version of the tenant data key that made it.</summary>
public sealed record ProtectedSecret(byte[] Cipher, int DekVersion);

/// <summary>
/// Encrypts a small secret under the tenant's own data key (Database doc 6.4). The <c>context</c> is bound into the encryption,
/// so ciphertext copied to another row, provider or tenant does not decrypt.
/// </summary>
public interface ITenantSecretProtector
{
    Task<ProtectedSecret> ProtectAsync(Guid tenantId, string plaintext, string context, CancellationToken ct = default);
    Task<string> UnprotectAsync(Guid tenantId, byte[] cipher, int dekVersion, string context, CancellationToken ct = default);
}

/// <summary>
/// Server-side request forgery defense at save time (Backend doc 5.1): is this address one we are willing to send traffic to?
/// The same rule is enforced again on every connection, so a name that later points somewhere internal is still stopped.
/// </summary>
public interface IOutboundUrlValidator
{
    Task<Result> ValidateAsync(string url, CancellationToken ct = default);
}
