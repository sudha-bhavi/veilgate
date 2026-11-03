using Microsoft.EntityFrameworkCore;
using VeilGate.Application.Abstractions;

namespace VeilGate.Infrastructure.Persistence;

/// <summary>
/// Reads security events for the console. Row-level security already limits it to the current tenant; the explicit tenant
/// filter is a second line of defense.
/// </summary>
public sealed class SecurityEventReader(VeilGateDbContext db, ITenantContext tenant) : ISecurityEventReader
{
    public async Task<(IReadOnlyList<SecurityEventView> Items, int Total)> ListAsync(
        SecurityEventFilter filter, int page, int pageSize, CancellationToken ct = default)
    {
        var tenantId = tenant.TenantId;
        var query = db.SecurityEvents.AsNoTracking().Where(e => e.TenantId == tenantId);

        if (filter.UserId is { } userId) query = query.Where(e => e.UserId == userId);
        if (filter.Severity is { } severity) query = query.Where(e => e.Severity == severity);
        // Npgsql only writes UTC values to timestamptz, so a time given with an offset is converted first.
        if (filter.From is { } from)
        {
            var fromUtc = from.ToUniversalTime();
            query = query.Where(e => e.OccurredAt >= fromUtc);
        }
        if (filter.To is { } to)
        {
            var toUtc = to.ToUniversalTime();
            query = query.Where(e => e.OccurredAt <= toUtc);
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        // Only ids are stored. Look the names up once per page.
        var userIds = rows.Where(r => r.UserId is not null).Select(r => r.UserId!.Value).Distinct().ToList();
        var clientIds = rows.Where(r => r.ClientId is not null).Select(r => r.ClientId!.Value).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => $"{u.DisplayName} ({u.Email})", ct);
        var clients = await db.ApiClients.AsNoTracking().Where(c => clientIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        var items = rows.Select(r => new SecurityEventView(
            r.Id, r.OccurredAt, r.Severity.ToString(), r.Type,
            r.UserId, r.UserId is { } u ? users.GetValueOrDefault(u) : null,
            r.ClientId, r.ClientId is { } c ? clients.GetValueOrDefault(c) : null,
            r.Ip, r.Details)).ToList();
        return (items, total);
    }

    public async Task<IReadOnlyList<SecurityEventPerson>> ListPeopleAsync(CancellationToken ct = default)
    {
        var tenantId = tenant.TenantId;
        return await db.Users.AsNoTracking().Where(u => u.TenantId == tenantId)
            .OrderBy(u => u.DisplayName)
            .Select(u => new SecurityEventPerson(u.Id, u.DisplayName, u.Email))
            .ToListAsync(ct);
    }
}
