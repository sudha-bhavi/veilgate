using VeilGate.Domain;

namespace VeilGate.Application.Abstractions;

public interface IRecoveryCodeRepository
{
    void AddRange(IEnumerable<RecoveryCode> codes);

    /// <summary>Finds one of this user's codes by its hash (used or not; the caller checks).</summary>
    Task<RecoveryCode?> FindByHashAsync(Guid userId, string codeHash, CancellationToken ct);

    /// <summary>
    /// Spends the code in ONE atomic step: true only for the single caller that actually changed it from unused to used.
    /// Two requests presenting the same code at the same moment can therefore never both succeed. (Loading a code, marking it used
    /// in memory and saving later is not safe: both requests load it as "unused".) The default below is the in-memory version, for
    /// hand-written test doubles (a mocking library replaces it, so tests that mock this interface set the method up themselves); the
    /// database repository replaces it with a conditional UPDATE.
    /// </summary>
    Task<bool> TrySpendAsync(RecoveryCode code, DateTimeOffset now, CancellationToken ct) =>
        Task.FromResult(code.MarkUsed(now).IsSuccess);

    Task<int> CountUnusedAsync(Guid userId, CancellationToken ct);

    /// <summary>All of one user's codes, used or not. Resetting someone's MFA removes them all.</summary>
    Task<List<RecoveryCode>> ListForUserAsync(Guid userId, CancellationToken ct);

    void RemoveRange(IEnumerable<RecoveryCode> codes);

    Task SaveChangesAsync(CancellationToken ct);
}