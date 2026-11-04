using Microsoft.EntityFrameworkCore;
using VeilGate.Application.Abstractions;
using VeilGate.Domain.Metrics;

namespace VeilGate.Infrastructure.Persistence;

/// <summary>
/// Reads the gateway requests that the metrics consumer wrote. Row-level security already limits every query to the current
/// tenant; the explicit tenant filter is a second line of defense and lets PostgreSQL use the (tenant_id, created_at) index.
/// </summary>
public sealed class GatewayRequestReader(VeilGateDbContext db, ITenantContext tenant) : IGatewayRequestReader
{
    public async Task<(IReadOnlyList<GatewayRequestView> Items, int Total)> ListAsync(
        GatewayRequestFilter filter, int page, int pageSize, CancellationToken ct = default)
    {
        var tenantId = tenant.TenantId;
        var query = db.GatewayRequests.AsNoTracking().Where(r => r.TenantId == tenantId);

        // Npgsql only writes UTC values to timestamptz, so a time given with an offset is converted first.
        if (filter.From is { } from)
        {
            var fromUtc = from.ToUniversalTime();
            query = query.Where(r => r.CreatedAt >= fromUtc);
        }
        if (filter.To is { } to)
        {
            var toUtc = to.ToUniversalTime();
            query = query.Where(r => r.CreatedAt <= toUtc);
        }
        if (!string.IsNullOrWhiteSpace(filter.Action)) query = query.Where(r => r.Action == filter.Action);
        if (filter.Status is not null) query = query.Where(r => r.Status == filter.Status);
        if (filter.ClientId is { } clientId) query = query.Where(r => r.ClientId == clientId);
        if (!string.IsNullOrWhiteSpace(filter.RequestId)) query = query.Where(r => r.RequestId == filter.RequestId);

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        var names = await NamesAsync(rows, ct);
        return (rows.Select(r => ToView(r, names)).ToList(), total);
    }

    public async Task<GatewayRequestView?> FindAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = tenant.TenantId;
        var row = await db.GatewayRequests.AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Id == id, ct);
        if (row is null) return null;
        return ToView(row, await NamesAsync([row], ct));
    }

    public async Task<IReadOnlyList<string>> ListActionsAsync(CancellationToken ct = default)
    {
        var tenantId = tenant.TenantId;
        return await db.GatewayRequests.AsNoTracking().Where(r => r.TenantId == tenantId)
            .Select(r => r.Action).Distinct().OrderBy(a => a).ToListAsync(ct);
    }

    // The table stores only ids. Look the names up once per page, for people and clients separately.
    private async Task<Dictionary<Guid, string>> NamesAsync(IReadOnlyList<GatewayRequestRecord> rows, CancellationToken ct)
    {
        var userIds = rows.Where(r => r.ActorType == "user").Select(r => r.ClientId).Distinct().ToList();
        var clientIds = rows.Where(r => r.ActorType == "client").Select(r => r.ClientId).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => $"{u.DisplayName} ({u.Email})", ct);
        foreach (var client in await db.ApiClients.AsNoTracking().Where(c => clientIds.Contains(c.Id)).ToListAsync(ct))
            names[client.Id] = client.Name;
        return names;
    }

    private static GatewayRequestView ToView(GatewayRequestRecord r, Dictionary<Guid, string> names) => new(
        r.Id, r.RequestId, r.CreatedAt, r.ActorType, r.ClientId, names.GetValueOrDefault(r.ClientId), r.Action, r.Status, r.Reason,
        r.Model, r.Provider, r.PolicyVersion, r.EntityCount, r.Entities, r.LatencyMs);
}
