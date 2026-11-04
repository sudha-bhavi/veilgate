using Microsoft.EntityFrameworkCore;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;

namespace VeilGate.Infrastructure.Persistence;

public sealed class MfaFactorRepository(VeilGateDbContext db) : IMfaFactorRepository
{
    // No "WHERE tenant_id = ..." here on purpose: PostgreSQL row-level security already limits
    // every query to the current tenant, so another tenant's factor is simply invisible.
    public Task<MfaFactor?> GetTotpForUserAsync(Guid userId, CancellationToken ct) =>
        db.MfaFactors.FirstOrDefaultAsync(f => f.UserId == userId && f.Type == MfaFactorType.Totp, ct);

    // One statement: "move the newest used window forward, but only if it is still behind this one".
    public async Task<bool> TryRecordUseAsync(MfaFactor factor, long counter, DateTimeOffset now, CancellationToken ct) =>
        await db.MfaFactors.Where(f => f.Id == factor.Id && f.IsConfirmed && f.LastUsedCounter < counter)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.LastUsedCounter, counter).SetProperty(f => f.UpdatedAt, now), ct) == 1;

    public void Add(MfaFactor factor) => db.MfaFactors.Add(factor);

    public void Remove(MfaFactor factor) => db.MfaFactors.Remove(factor);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}