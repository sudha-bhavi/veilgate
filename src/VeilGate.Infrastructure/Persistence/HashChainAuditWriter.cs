using Microsoft.EntityFrameworkCore;
using VeilGate.Application.Abstractions;
using VeilGate.Domain.Audit;

namespace VeilGate.Infrastructure.Persistence;

public sealed class HashChainAuditWriter(VeilGateDbContext db) : IAuditWriter
{
    public async Task WriteAsync(AuditEvent auditEvent, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // 1. Take the tenant's lock. Other writers for this tenant wait here until we commit. Other tenants are not affected.
            var lockKey = auditEvent.TenantId.ToString();
            await db.Database.ExecuteSqlAsync($"select pg_advisory_xact_lock(hashtext({lockKey}))", ct);

            // 2. Read the end of the chain.
            var last = await db.AuditEvents.AsNoTracking()
                .Where(x => x.TenantId == auditEvent.TenantId)
                .OrderByDescending(x => x.Seq)
                .Select(x => new { x.Seq, x.Hash })
                .FirstOrDefaultAsync(ct);

            // 3. Place the new event after it and save. The unique index on (tenant_id, seq) is the second line of defense.
            auditEvent.Seal((last?.Seq ?? 0) + 1, last?.Hash ?? AuditEvent.Genesis);
            db.AuditEvents.Add(auditEvent);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            db.Entry(auditEvent).State = EntityState.Detached;   // a failed event must not stay queued in this context
            throw;
        }
    }
}