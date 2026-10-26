using Microsoft.EntityFrameworkCore;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;

namespace VeilGate.Infrastructure.Persistence;

public sealed class SessionRepository(VeilGateDbContext db) : ISessionRepository
{
    // No tenant filter: row-level security already limits every query to the current tenant.
    public void Add(Session session) => db.Sessions.Add(session);

    public Task<Session?> FindByRefreshHashAsync(string refreshHash, CancellationToken ct) =>
        db.Sessions.FirstOrDefaultAsync(s => s.CurrentRefreshHash == refreshHash || s.PreviousRefreshHash == refreshHash, ct);

    // The idle check (LastSeenAt + 8h) stays in Session.IsActive, so the rule lives in one place.
    public async Task<List<Session>> ListActiveForUserAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var candidates = await db.Sessions
            .Where(s => s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > now)
            .ToListAsync(ct);
        return candidates.Where(s => s.IsActive(now)).ToList();
    }

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}