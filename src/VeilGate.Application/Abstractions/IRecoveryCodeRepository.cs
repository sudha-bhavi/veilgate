using VeilGate.Domain;

namespace VeilGate.Application.Abstractions;

public interface IRecoveryCodeRepository
{
    void AddRange(IEnumerable<RecoveryCode> codes);

    /// <summary>Finds one of this user's codes by its hash (used or not; the caller checks).</summary>
    Task<RecoveryCode?> FindByHashAsync(Guid userId, string codeHash, CancellationToken ct);

    Task<int> CountUnusedAsync(Guid userId, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}