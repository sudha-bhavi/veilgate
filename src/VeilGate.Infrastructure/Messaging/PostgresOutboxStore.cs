using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VeilGate.Application.Abstractions;
using VeilGate.Infrastructure.Persistence;

namespace VeilGate.Infrastructure.Messaging;

/// <summary>Calls the four outbox functions (see CreateOutboxFunctions). No tenant is set: the publisher serves every tenant.</summary>
public sealed class PostgresOutboxStore(VeilGateDbContext db) : IOutboxStore
{
    public async Task<IReadOnlyList<ClaimedMessage>> ClaimDueAsync(int limit, TimeSpan lease, CancellationToken ct = default)
    {
        var seconds = (int)lease.TotalSeconds;
        var rows = await db.Database.SqlQuery<ClaimedRow>($"""
            SELECT msg_id AS "Id", msg_tenant_id AS "TenantId", msg_topic AS "Topic", msg_key AS "Key", msg_payload AS "Payload",
                   msg_headers AS "Headers", msg_attempts AS "Attempts", msg_created_at AS "CreatedAt"
            FROM outbox_claim_due({limit}, {seconds})
            """).ToListAsync(ct);

        return rows.Select(r => new ClaimedMessage(
            r.Id, r.TenantId, r.Topic, r.Key, r.Payload,
            JsonSerializer.Deserialize<Dictionary<string, string>>(r.Headers) ?? new Dictionary<string, string>(),
            r.Attempts, r.CreatedAt)).ToList();
    }

    public async Task MarkSentAsync(Guid id, CancellationToken ct = default) =>
        await db.Database.ExecuteSqlAsync($"SELECT outbox_mark_sent({id})", ct);

    public async Task MarkFailedAsync(Guid id, string errorCode, TimeSpan backoffCap, CancellationToken ct = default)
    {
        var cap = (int)backoffCap.TotalSeconds;
        await db.Database.ExecuteSqlAsync($"SELECT outbox_mark_failed({id}, {errorCode}, {cap})", ct);
    }

    public async Task MarkDeadAsync(Guid id, string errorCode, CancellationToken ct = default) =>
        await db.Database.ExecuteSqlAsync($"SELECT outbox_mark_dead({id}, {errorCode})", ct);

    private sealed class ClaimedRow
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public string Topic { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
        public string Payload { get; set; } = string.Empty;
        public string Headers { get; set; } = "{}";
        public int Attempts { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }
}
