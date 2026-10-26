using Microsoft.EntityFrameworkCore;
using VeilGate.Application.Abstractions;
using VeilGate.Domain.Audit;

namespace VeilGate.Infrastructure.Persistence;

public sealed class SecurityEventWriter(VeilGateDbContext db) : ISecurityEventWriter
{
    public async Task WriteAsync(SecurityEvent securityEvent, CancellationToken ct = default)
    {
        db.SecurityEvents.Add(securityEvent);
        try { await db.SaveChangesAsync(ct); }
        catch
        {
            db.Entry(securityEvent).State = EntityState.Detached;   // a failed event must not stay queued in this context
            throw;
        }
    }
}
