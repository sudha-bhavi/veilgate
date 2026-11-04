using Microsoft.EntityFrameworkCore;
using VeilGate.Application.Abstractions;
using VeilGate.Domain.Evaluation;
using VeilGate.Domain.Metrics;

namespace VeilGate.Infrastructure.Persistence;

/// <summary>
/// Reads the hourly counters the metrics consumer wrote. A tenant has at most one row per client per hour, so a month is a few
/// thousand rows at most and is summed here. Row-level security limits every query to the current tenant; the explicit tenant
/// filter is a second line of defense.
/// </summary>
public sealed class DashboardReader(VeilGateDbContext db, ITenantContext tenant) : IDashboardReader
{
    private IQueryable<MetricsHour> Rows(DateTimeOffset from, DateTimeOffset to)
    {
        var tenantId = tenant.TenantId;
        var fromUtc = from.ToUniversalTime();   // Npgsql only writes UTC values to timestamptz
        var toUtc = to.ToUniversalTime();
        return db.MetricsHourly.AsNoTracking().Where(m => m.TenantId == tenantId && m.Hour >= fromUtc && m.Hour < toUtc);
    }

    public async Task<DashboardTotals> TotalsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var rows = await Rows(from, to).ToListAsync(ct);
        var byType = new Dictionary<string, int>();
        var buckets = new Dictionary<string, int>();
        foreach (var row in rows)
        {
            byType = MetricsRollup.Add(byType, row.EntitiesByType);
            buckets = MetricsRollup.Add(buckets, row.LatencyBuckets);
        }
        return new DashboardTotals(rows.Sum(r => (long)r.Requests), rows.Sum(r => (long)r.Blocked), rows.Sum(r => (long)r.Failed),
            rows.Sum(r => r.Entities), byType, buckets);
    }

    public async Task<IReadOnlyList<ClientTotal>> TopClientsAsync(DateTimeOffset from, DateTimeOffset to, int take, CancellationToken ct = default)
    {
        var rows = await Rows(from, to).Select(m => new { m.ClientId, m.Requests }).ToListAsync(ct);
        var top = rows.GroupBy(r => r.ClientId)
            .Select(g => (ClientId: g.Key, Requests: g.Sum(r => (long)r.Requests)))
            .OrderByDescending(t => t.Requests).ThenBy(t => t.ClientId).Take(take).ToList();

        // The counters hold only ids, and a caller is an API client or a person (the Playground). Look both up.
        var ids = top.Select(t => t.ClientId).ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        foreach (var client in await db.ApiClients.AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync(ct))
            names[client.Id] = client.Name;

        return top.Select(t => new ClientTotal(t.ClientId, names.GetValueOrDefault(t.ClientId), t.Requests)).ToList();
    }

    public async Task<IReadOnlyList<HourlyPoint>> HourlyAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var rows = await Rows(from, to).ToListAsync(ct);
        return rows.GroupBy(r => r.Hour).OrderBy(g => g.Key)
            .Select(g => new HourlyPoint(g.Key, g.Sum(r => (long)r.Requests), g.Sum(r => r.Entities),
                g.Aggregate(new Dictionary<string, int>(), (sum, r) => MetricsRollup.Add(sum, r.LatencyBuckets))))
            .ToList();
    }

    public async Task<double?> LatestLeakRateAsync(CancellationToken ct = default)
    {
        var tenantId = tenant.TenantId;
        var latest = await db.EvalRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.State == EvalRunState.Succeeded)
            .OrderByDescending(r => r.FinishedAt)
            .Select(r => r.Summary)
            .FirstOrDefaultAsync(ct);
        return latest?.LeakRate;
    }
}
