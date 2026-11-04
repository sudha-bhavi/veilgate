using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VeilGate.Application.Abstractions;
using VeilGate.Domain.Messaging;
using VeilGate.Domain.Metrics;
using VeilGate.Infrastructure.Persistence;

namespace VeilGate.Infrastructure.Messaging;

/// <summary>
/// The idempotent consumer logic. Kafka delivers at least once, so the first thing each message does is record its id in
/// processed_messages; if that row already exists the message was handled before and is skipped. That insert, the request row
/// and the counter update share one transaction, so a crash in the middle leaves nothing half done.
/// The tenant is set from the event before the first database call, so row-level security applies to every write.
/// </summary>
public sealed class GatewayEventProjector(IServiceScopeFactory scopes, IClock clock, ILogger<GatewayEventProjector> logger) : IGatewayEventProjector
{
    public const string ConsumerName = "metrics-aggregator";

    public async Task<ProjectionOutcome> ProjectAsync(Guid messageId, string payloadJson, CancellationToken ct = default)
    {
        var parsed = GatewayRequestEvent.Parse(payloadJson);
        if (parsed.IsFailure)
        {
            logger.LogWarning("Message {MessageId} is not a valid gateway event: {Code}", messageId, parsed.Error.Code);   // never the payload
            return ProjectionOutcome.Rejected;
        }
        var e = parsed.Value;

        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(e.TenantId);
        var db = scope.ServiceProvider.GetRequiredService<VeilGateDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);   // disposed without a commit means rolled back

        // 1. Have we seen this message? Two copies at the same moment are told apart here: the second waits for the first, then finds the row.
        var now = clock.UtcNow;
        var recorded = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO processed_messages (consumer, message_id, tenant_id, processed_at)
            VALUES ({ConsumerName}, {messageId}, {e.TenantId}, {now})
            ON CONFLICT DO NOTHING
            """, ct);
        if (recorded == 0) return ProjectionOutcome.Duplicate;

        // 2. The row for the Requests page.
        db.GatewayRequests.Add(GatewayRequestRecord.From(messageId, e));
        await db.SaveChangesAsync(ct);

        // 3. The hour's counters.
        await AddToHourAsync(db, e, ct);

        await tx.CommitAsync(ct);
        return ProjectionOutcome.Applied;
    }

    private static async Task AddToHourAsync(VeilGateDbContext db, GatewayRequestEvent e, CancellationToken ct)
    {
        var delta = MetricsRollup.DeltaFor(e);
        var empty = "{}";

        // Make sure the hour's row exists, then lock it. Another consumer working on the same hour waits here until we commit,
        // and then reads our result, so no update is lost.
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO metrics_hourly (tenant_id, hour, client_id, requests, blocked, failed, entities, entities_by_type, latency_buckets)
            VALUES ({e.TenantId}, {delta.Hour}, {delta.ClientId}, 0, 0, 0, 0, {empty}::jsonb, {empty}::jsonb)
            ON CONFLICT DO NOTHING
            """, ct);

        var rows = await db.MetricsHourly.FromSql($"""
            SELECT * FROM metrics_hourly
            WHERE tenant_id = {e.TenantId} AND hour = {delta.Hour} AND client_id = {delta.ClientId}
            FOR UPDATE
            """).AsNoTracking().ToListAsync(ct);

        var merged = MetricsRollup.Merge(rows.Single(), e.TenantId, delta);
        var byType = JsonSerializer.Serialize(merged.EntitiesByType);
        var buckets = JsonSerializer.Serialize(merged.LatencyBuckets);

        await db.Database.ExecuteSqlAsync($"""
            UPDATE metrics_hourly
            SET requests = {merged.Requests}, blocked = {merged.Blocked}, failed = {merged.Failed}, entities = {merged.Entities},
                entities_by_type = {byType}::jsonb, latency_buckets = {buckets}::jsonb
            WHERE tenant_id = {e.TenantId} AND hour = {delta.Hour} AND client_id = {delta.ClientId}
            """, ct);
    }
}
