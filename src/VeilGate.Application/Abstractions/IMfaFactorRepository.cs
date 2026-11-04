using VeilGate.Domain;

namespace VeilGate.Application.Abstractions;

/// <summary>Loads and saves MFA factors of the current tenant (row-level security enforces the tenant).</summary>
public interface IMfaFactorRepository
{
    Task<MfaFactor?> GetTotpForUserAsync(Guid userId, CancellationToken ct);
    /// <summary>
    /// Records that a code from this 30-second window was accepted, in ONE atomic step: true only for the single caller that moved the
    /// factor's "newest window used" forward. Two requests with the same code at the same moment can never both succeed. The default is the
    /// in-memory version, for hand-written test doubles (a mocking library replaces it, so tests that mock this interface set the method up
    /// themselves); the database repository replaces it with a conditional UPDATE.
    /// </summary>
    Task<bool> TryRecordUseAsync(MfaFactor factor, long counter, DateTimeOffset now, CancellationToken ct) =>
        Task.FromResult(factor.RecordUse(counter, now).IsSuccess);

    void Add(MfaFactor factor);
    void Remove(MfaFactor factor);
    Task SaveChangesAsync(CancellationToken ct);
}