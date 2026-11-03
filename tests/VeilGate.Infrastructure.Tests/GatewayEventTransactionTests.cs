using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.Domain.Audit;
using VeilGate.Domain.Messaging;
using VeilGate.Infrastructure.Persistence;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Infrastructure.Tests;

/// <summary>The pipeline queues the gateway event and then writes the audit row. These tests do the same two steps against a real database.</summary>
[Collection("postgres")]
public class GatewayEventTransactionTests(PostgresFixture pg)
{
    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

    private VeilGateDbContext AppContext(Guid tenantId)
    {
        var tenant = new TenantContext();
        tenant.Set(tenantId);
        var options = new DbContextOptionsBuilder<VeilGateDbContext>()
            .UseNpgsql(pg.AppConnection)
            .AddInterceptors(new TenantConnectionInterceptor(tenant))
            .Options;
        return new VeilGateDbContext(options);
    }

    private async Task<Guid> SeedTenant()
    {
        var t = Tenant.Create($"GwEvent {Guid.NewGuid():N}"[..20]).Value;
        await using var db = pg.AdminContext();
        db.Add(t);
        await db.SaveChangesAsync();
        return t.Id;
    }

    private static AuditEvent Audit(Guid tenantId) => AuditEvent.Create(tenantId, DateTimeOffset.UtcNow, "client", Guid.NewGuid(),
        "gateway.chat", "gateway_request", null, "success", "203.0.113.9", new Dictionary<string, string> { ["requestId"] = "req_1" }).Value;

    private static GatewayRequestEvent Announcement(Guid tenantId) => GatewayRequestEvent.Create(
        "req_1", DateTimeOffset.UtcNow, tenantId, Guid.NewGuid(), "client", "gateway.chat", "success",
        new Dictionary<string, int> { ["PHONE"] = 2 }, 120, "gpt-4o-mini", "mock-llm", "7").Value;

    private async Task<(int Audit, int Outbox)> Counts(Guid tenantId)
    {
        await using var db = pg.AdminContext();   // the owner sees every row
        return (await db.AuditEvents.CountAsync(x => x.TenantId == tenantId), await db.OutboxMessages.CountAsync(x => x.TenantId == tenantId));
    }

    [Fact]
    public async Task The_audit_row_and_the_announcement_are_saved_together()
    {
        var tenantId = await SeedTenant();

        await using (var db = AppContext(tenantId))
        {
            new EfOutbox(db, new FixedClock()).Enqueue(tenantId, OutboxTopics.GatewayRequests, Announcement(tenantId));
            await new HashChainAuditWriter(db).WriteAsync(Audit(tenantId));
        }

        (await Counts(tenantId)).Should().Be((1, 1));
    }

    [Fact]
    public async Task The_saved_announcement_holds_the_event_as_json()
    {
        var tenantId = await SeedTenant();
        await using (var db = AppContext(tenantId))
        {
            new EfOutbox(db, new FixedClock()).Enqueue(tenantId, OutboxTopics.GatewayRequests, Announcement(tenantId));
            await new HashChainAuditWriter(db).WriteAsync(Audit(tenantId));
        }

        await using var read = AppContext(tenantId);
        var message = await read.OutboxMessages.AsNoTracking().SingleAsync();

        message.Topic.Should().Be(OutboxTopics.GatewayRequests);
        using var json = System.Text.Json.JsonDocument.Parse(message.Payload);   // jsonb rewrites spacing, so read it as JSON, not as text
        json.RootElement.GetProperty("action").GetString().Should().Be("gateway.chat");
        json.RootElement.GetProperty("entities").GetProperty("PHONE").GetInt32().Should().Be(2);
        json.RootElement.GetProperty("latencyMs").GetInt64().Should().Be(120);
    }

    [Fact]
    public async Task If_the_audit_row_cannot_be_saved_the_announcement_is_not_saved_either()
    {
        var a = await SeedTenant();
        var b = await SeedTenant();

        await using (var db = AppContext(a))
        {
            new EfOutbox(db, new FixedClock()).Enqueue(a, OutboxTopics.GatewayRequests, Announcement(a));   // valid for tenant a
            var act = () => new HashChainAuditWriter(db).WriteAsync(Audit(b));                              // refused: a cannot write into b's log
            await act.Should().ThrowAsync<DbUpdateException>();
        }

        (await Counts(a)).Should().Be((0, 0));
        (await Counts(b)).Should().Be((0, 0));
    }
}
