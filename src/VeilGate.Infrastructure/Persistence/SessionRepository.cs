using Microsoft.EntityFrameworkCore;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;

namespace VeilGate.Infrastructure.Persistence;

public sealed class SessionRepository(VeilGateDbContext db) : ISessionRepository
{
    // No tenant filter: row-level security already limits every query to the current tenant.
    public void Add(Session session) => db.Sessions.Add(session);
        public async Task<Guid?> FindTenantByRefreshHashAsync(string refreshHash, CancellationToken ct)
    {
        // The one session query that runs before a tenant is known. EF's scalar SqlQuery needs the column named "Value".
        var rows = await db.Database
            .SqlQuery<Guid?>($"select auth_find_session_tenant({refreshHash}) as \"Value\"")
            .ToListAsync(ct);
        return rows.FirstOrDefault();
    }

    public async Task<bool> TryRotateAsync(Session session, string presentedHash, string newHash, DateTimeOffset now, CancellationToken ct) =>
        await db.Sessions.Where(s => s.Id == session.Id && s.CurrentRefreshHash == presentedHash && s.RevokedAt == null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.PreviousRefreshHash, presentedHash)
                .SetProperty(s => s.CurrentRefreshHash, newHash)
                .SetProperty(s => s.LastSeenAt, now), ct) == 1;

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

    public Task<SessionState?> GetStateAsync(Guid sessionId, CancellationToken ct) =>
        db.Sessions.AsNoTracking().Where(s => s.Id == sessionId)
            .Select(s => new SessionState(s.RevokedAt, s.ExpiresAt))
            .FirstOrDefaultAsync(ct);

    public Task<Session?> FindForUserAsync(Guid sessionId, Guid userId, CancellationToken ct) =>
        db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId, ct);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}