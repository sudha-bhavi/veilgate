using VeilGate.Domain;

namespace VeilGate.Application.Abstractions;

/// <summary>Loads and saves MFA factors of the current tenant (row-level security enforces the tenant).</summary>
public interface IMfaFactorRepository
{
    Task<MfaFactor?> GetTotpForUserAsync(Guid userId, CancellationToken ct);
    void Add(MfaFactor factor);
    void Remove(MfaFactor factor);
    Task SaveChangesAsync(CancellationToken ct);
}