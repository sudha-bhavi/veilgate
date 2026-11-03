using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.Domain.Audit;
using VeilGate.Domain.Messaging;
using VeilGate.Infrastructure.Persistence;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Infrastructure.Tests;

[Collection("postgres")]
public class OutboxTests(PostgresFixture pg)
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
        var t = Tenant.Create($"Outbox {Guid.NewGuid():N}"[..20]).Value;
        await using var db = pg.AdminContext();
        db.Add(t);
        await db.SaveChangesAsync();
        return t.Id;
    }

    private static object Payload() => new { requestId = "req_1", action = "gateway.chat", result = "success" };

    [Fact]
    public async Task A_message_survives_a_round_trip_to_the_database()
    {
        var tenantId = await SeedTenant();
        await using (var db = AppContext(tenantId))
        {
            new EfOutbox(db, new FixedClock()).Enqueue(tenantId, OutboxTopics.GatewayRequests, Payload()).IsSuccess.Should().BeTrue();
            await db.SaveChangesAsync();
        }

        await using var read = AppContext(tenantId);
        var loaded = await read.OutboxMessages.AsNoTracking().SingleAsync();

        loaded.Topic.Should().Be(OutboxTopics.GatewayRequests);
        loaded.Key.Should().Be(tenantId.ToString());
        loaded.Payload.Should().Contain("\"requestId\"").And.Contain("req_1");
        loaded.Headers["messageId"].Should().Be(loaded.Id.ToString());
        loaded.Headers["tenantId"].Should().Be(tenantId.ToString());
        loaded.SentAt.Should().BeNull();
        loaded.Attempts.Should().Be(0);
    }

    [Fact]
    public async Task The_message_is_saved_in_the_same_save_as_the_work()
    {
        var tenantId = await SeedTenant();
        var securityEvent = SecurityEvent.Create(tenantId, DateTimeOffset.UtcNow, Severity.High, "leak.gate_tripped").Value;

        await using (var db = AppContext(tenantId))
        {
            db.SecurityEvents.Add(securityEvent);
            new EfOutbox(db, new FixedClock()).Enqueue(tenantId, OutboxTopics.GatewayRequests, Payload());
            await db.SaveChangesAsync();   // one save: the work and its message
        }

        await using var read = AppContext(tenantId);
        (await read.SecurityEvents.CountAsync(x => x.Id == securityEvent.Id)).Should().Be(1);
        (await read.OutboxMessages.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_rolled_back_transaction_leaves_no_message_behind()
    {
        var tenantId = await SeedTenant();
        var securityEvent = SecurityEvent.Create(tenantId, DateTimeOffset.UtcNow, Severity.High, "leak.gate_tripped").Value;

        await using (var db = AppContext(tenantId))
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            db.SecurityEvents.Add(securityEvent);
            new EfOutbox(db, new FixedClock()).Enqueue(tenantId, OutboxTopics.GatewayRequests, Payload());
            await db.SaveChangesAsync();
            await tx.RollbackAsync();   // the work is undone, so its message must go with it
        }

        await using var read = AppContext(tenantId);
        (await read.SecurityEvents.CountAsync(x => x.Id == securityEvent.Id)).Should().Be(0);
        (await read.OutboxMessages.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_unknown_topic_is_refused_and_nothing_is_queued()
    {
        var tenantId = await SeedTenant();
        await using var db = AppContext(tenantId);

        new EfOutbox(db, new FixedClock()).Enqueue(tenantId, "vg.other.v1", Payload()).IsFailure.Should().BeTrue();
        await db.SaveChangesAsync();

        (await db.OutboxMessages.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Another_tenant_cannot_see_the_message()
    {
        var a = await SeedTenant();
        var b = await SeedTenant();
        await using (var db = AppContext(a))
        {
            new EfOutbox(db, new FixedClock()).Enqueue(a, OutboxTopics.GatewayRequests, Payload());
            await db.SaveChangesAsync();
        }

        await using var other = AppContext(b);
        (await other.OutboxMessages.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_tenant_cannot_queue_a_message_for_another_tenant()
    {
        var a = await SeedTenant();
        var b = await SeedTenant();

        await using var db = AppContext(b);   // connected as tenant b, message says tenant a
        new EfOutbox(db, new FixedClock()).Enqueue(a, OutboxTopics.GatewayRequests, Payload());
        var act = () => db.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task The_app_role_cannot_update_or_delete_messages()
    {
        var tenantId = await SeedTenant();
        await using var db = AppContext(tenantId);
        new EfOutbox(db, new FixedClock()).Enqueue(tenantId, OutboxTopics.GatewayRequests, Payload());
        await db.SaveChangesAsync();

        var update = () => db.Database.ExecuteSqlRawAsync("update outbox_messages set attempts = 5");
        await update.Should().ThrowAsync<PostgresException>().Where(e => e.SqlState == "42501");   // permission denied

        var delete = () => db.Database.ExecuteSqlRawAsync("delete from outbox_messages");
        await delete.Should().ThrowAsync<PostgresException>().Where(e => e.SqlState == "42501");
    }
}
