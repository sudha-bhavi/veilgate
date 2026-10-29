using Microsoft.EntityFrameworkCore;
using VeilGate.Application.Abstractions;

namespace VeilGate.Infrastructure.Persistence;

/// <summary>
/// Reads the audit log for the console. Row-level security already limits it to the current tenant; the explicit tenant
/// filter is a second line of defense and lets PostgreSQL use the (tenant_id, seq) index.
/// </summary>
public sealed class AuditEventReader(VeilGateDbContext db, ITenantContext tenant) : IAuditEventReader
{
    public async Task<(IReadOnlyList<AuditEventView> Items, int Total)> ListAsync(
        AuditEventFilter filter, int page, int pageSize, CancellationToken ct = default)
    {
        var tenantId = tenant.TenantId;
        var query = db.AuditEvents.AsNoTracking().Where(e => e.TenantId == tenantId);

        // Npgsql only writes UTC values to timestamptz, so a time given with an offset (2026-10-29T10:00+02:00) is converted first.
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
        if (!string.IsNullOrWhiteSpace(filter.Action)) query = query.Where(e => e.Action == filter.Action);
        if (filter.ActorType is not null) query = query.Where(e => e.ActorType == filter.ActorType);
        if (filter.ActorId is { } actorId) query = query.Where(e => e.ActorId == actorId);
        if (!string.IsNullOrWhiteSpace(filter.ResourceType)) query = query.Where(e => e.ResourceType == filter.ResourceType);
        if (filter.ResourceId is { } resourceId) query = query.Where(e => e.ResourceId == resourceId);
        if (filter.Result is not null) query = query.Where(e => e.Result == filter.Result);

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(e => e.Seq).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        // The log stores only ids. Look the names up once per page, for people and clients separately.
        var userIds = rows.Where(r => r.ActorType == "user" && r.ActorId is not null).Select(r => r.ActorId!.Value).Distinct().ToList();
        var clientIds = rows.Where(r => r.ActorType == "client" && r.ActorId is not null).Select(r => r.ActorId!.Value).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => $"{u.DisplayName} ({u.Email})", ct);
        var clients = await db.ApiClients.AsNoTracking().Where(c => clientIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        var items = rows.Select(r => new AuditEventView(
            r.Id, r.Seq, r.OccurredAt, r.ActorType, r.ActorId, NameOf(r.ActorType, r.ActorId), r.Action,
            r.ResourceType, r.ResourceId, r.Result, r.Ip, r.Details, r.Hash)).ToList();
        return (items, total);

        string? NameOf(string actorType, Guid? id) => id is not { } key ? null : actorType switch
        {
            "user" => users.GetValueOrDefault(key),
            "client" => clients.GetValueOrDefault(key),
            _ => null,
        };
    }

    public async Task<IReadOnlyList<string>> ListActionsAsync(CancellationToken ct = default)
    {
        var tenantId = tenant.TenantId;
        return await db.AuditEvents.AsNoTracking().Where(e => e.TenantId == tenantId)
            .Select(e => e.Action).Distinct().OrderBy(a => a).ToListAsync(ct);
    }
}
