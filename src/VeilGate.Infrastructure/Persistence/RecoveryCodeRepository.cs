using Microsoft.EntityFrameworkCore;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;

namespace VeilGate.Infrastructure.Persistence;

public sealed class RecoveryCodeRepository(VeilGateDbContext db) : IRecoveryCodeRepository
{
    // As with MFA factors: no tenant filter here, because row-level security already limits every query.
    public void AddRange(IEnumerable<RecoveryCode> codes) => db.RecoveryCodes.AddRange(codes);

    public Task<RecoveryCode?> FindByHashAsync(Guid userId, string codeHash, CancellationToken ct) =>
        db.RecoveryCodes.FirstOrDefaultAsync(c => c.UserId == userId && c.CodeHash == codeHash, ct);

    public Task<int> CountUnusedAsync(Guid userId, CancellationToken ct) =>
        db.RecoveryCodes.CountAsync(c => c.UserId == userId && c.UsedAt == null, ct);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}