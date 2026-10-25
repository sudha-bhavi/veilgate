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

    public void Add(MfaFactor factor) => db.MfaFactors.Add(factor);

    public void Remove(MfaFactor factor) => db.MfaFactors.Remove(factor);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}