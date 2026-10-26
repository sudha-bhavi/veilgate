using Microsoft.EntityFrameworkCore;
using VeilGate.Application.Abstractions;
using VeilGate.Domain.Audit;

namespace VeilGate.Infrastructure.Persistence;

public sealed class AuditChainVerifier(VeilGateDbContext db) : IAuditChainVerifier
{
    private const int BatchSize = 5000;   // Backend doc 6.4

    public async Task<ChainVerification> VerifyAsync(Guid tenantId, long? fromSeq = null, long? toSeq = null, CancellationToken ct = default)
    {
        var start = Math.Max(fromSeq ?? 1, 1);
        var expectedSeq = start;
        var prevHash = AuditEvent.Genesis;

        if (start > 1)
        {
            // Starting mid-chain: the event just before the range tells us what the first link should point to.
            var before = await db.AuditEvents.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.Seq == start - 1)
                .Select(x => x.Hash).FirstOrDefaultAsync(ct);
            if (before is null) return new ChainVerification(false, 0, start - 1, null, null);
            prevHash = before;
        }

        long verified = 0;
        var cursor = start - 1;
        while (true)
        {
            var batch = await db.AuditEvents.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.Seq > cursor && (toSeq == null || x.Seq <= toSeq))
                .OrderBy(x => x.Seq)
                .Take(BatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0) break;

            foreach (var row in batch)
            {
                // 1. Numbers must be gap-free. A missing number means a deleted row.
                if (row.Seq != expectedSeq) return new ChainVerification(false, verified, expectedSeq, null, null);

                // 2. The row must point at the hash of the row before it.
                if (row.PrevHash != prevHash) return new ChainVerification(false, verified, row.Seq, prevHash, row.PrevHash);

                // 3. The stored hash must match the row's current contents. An edited row fails here.
                var recomputed = AuditHasher.Compute(row);
                if (recomputed != row.Hash) return new ChainVerification(false, verified, row.Seq, recomputed, row.Hash);

                prevHash = row.Hash;
                expectedSeq++;
                verified++;
            }
            cursor = batch[^1].Seq;
        }

        // A range that ends early means rows at the end of the range are missing.
        if (toSeq is not null && expectedSeq <= toSeq) return new ChainVerification(false, verified, expectedSeq, null, null);

        return new ChainVerification(true, verified, null, null, null);
    }
}