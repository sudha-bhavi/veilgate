using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using VeilGate.Domain;
using VeilGate.Domain.Metrics;
using VeilGate.Infrastructure.Persistence;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Infrastructure.Tests;

[Collection("postgres")]
public class MetricsTablesTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Hour = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);

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
        var t = Tenant.Create($"Metrics {Guid.NewGuid():N}"[..20]).Value;
        await using var db = pg.AdminContext();
        db.Add(t);
        await db.SaveChangesAsync();
        return t.Id;
    }

    private static GatewayRequestRecord Request(Guid tenantId) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, ClientId = Guid.NewGuid(), ActorType = "client", RequestId = "req_1",
        Action = "gateway.chat", Status = "success", Model = "gpt-4o-mini", Provider = "mock-llm", PolicyVersion = "7",
        EntityCount = 3, Entities = new Dictionary<string, int> { ["PHONE"] = 2, ["SSN"] = 1 }, LatencyMs = 120, CreatedAt = Hour,
    };

    private static MetricsHour Counters(Guid tenantId, Guid clientId) => new()
    {
        TenantId = tenantId, Hour = Hour, ClientId = clientId, Requests = 5, Blocked = 1, Failed = 0, Entities = 9,
        EntitiesByType = new Dictionary<string, int> { ["PHONE"] = 9 }, LatencyBuckets = new Dictionary<string, int> { ["100"] = 4, ["250"] = 1 },
    };

    [Fact]
    public async Task A_request_row_survives_a_round_trip()
    {
        var tenantId = await SeedTenant();
        var row = Request(tenantId);
        await using (var db = AppContext(tenantId)) { db.GatewayRequests.Add(row); await db.SaveChangesAsync(); }

        await using var read = AppContext(tenantId);
        var loaded = await read.GatewayRequests.AsNoTracking().SingleAsync(x => x.Id == row.Id);

        loaded.Action.Should().Be("gateway.chat");
        loaded.Entities.Should().BeEquivalentTo(new Dictionary<string, int> { ["PHONE"] = 2, ["SSN"] = 1 });
        loaded.LatencyMs.Should().Be(120);
        loaded.CreatedAt.Should().Be(Hour);
    }

    [Fact]
    public async Task Hourly_counters_survive_a_round_trip()
    {
        var tenantId = await SeedTenant();
        var clientId = Guid.NewGuid();
        await using (var db = AppContext(tenantId)) { db.MetricsHourly.Add(Counters(tenantId, clientId)); await db.SaveChangesAsync(); }

        await using var read = AppContext(tenantId);
        var loaded = await read.MetricsHourly.AsNoTracking().SingleAsync(x => x.ClientId == clientId);

        loaded.Requests.Should().Be(5);
        loaded.EntitiesByType.Should().BeEquivalentTo(new Dictionary<string, int> { ["PHONE"] = 9 });
        loaded.LatencyBuckets.Should().BeEquivalentTo(new Dictionary<string, int> { ["100"] = 4, ["250"] = 1 });
    }

    [Fact]
    public async Task A_tenant_cannot_see_another_tenants_requests_counters_or_processed_messages()
    {
        var a = await SeedTenant();
        var b = await SeedTenant();
        await using (var db = AppContext(a))
        {
            db.GatewayRequests.Add(Request(a));
            db.MetricsHourly.Add(Counters(a, Guid.NewGuid()));
            db.ProcessedMessages.Add(new ProcessedMessage { Consumer = "test", MessageId = Guid.NewGuid(), TenantId = a, ProcessedAt = Hour });
            await db.SaveChangesAsync();
        }

        await using var other = AppContext(b);
        (await other.GatewayRequests.CountAsync()).Should().Be(0);
        (await other.MetricsHourly.CountAsync()).Should().Be(0);
        (await other.ProcessedMessages.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_tenant_cannot_write_a_row_for_another_tenant()
    {
        var a = await SeedTenant();
        var b = await SeedTenant();

        await using var db = AppContext(b);   // connected as b, row says a
        db.GatewayRequests.Add(Request(a));
        var act = () => db.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task The_same_message_cannot_be_recorded_twice_for_one_consumer()
    {
        var tenantId = await SeedTenant();
        var messageId = Guid.NewGuid();
        await using (var db = AppContext(tenantId))
        {
            db.ProcessedMessages.Add(new ProcessedMessage { Consumer = "metrics", MessageId = messageId, TenantId = tenantId, ProcessedAt = Hour });
            await db.SaveChangesAsync();
        }

        await using var again = AppContext(tenantId);
        again.ProcessedMessages.Add(new ProcessedMessage { Consumer = "metrics", MessageId = messageId, TenantId = tenantId, ProcessedAt = Hour });
        var act = () => again.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Two_different_consumers_may_each_record_the_same_message()
    {
        var tenantId = await SeedTenant();
        var messageId = Guid.NewGuid();
        await using var db = AppContext(tenantId);
        db.ProcessedMessages.Add(new ProcessedMessage { Consumer = "metrics", MessageId = messageId, TenantId = tenantId, ProcessedAt = Hour });
        db.ProcessedMessages.Add(new ProcessedMessage { Consumer = "alerts", MessageId = messageId, TenantId = tenantId, ProcessedAt = Hour });

        var act = () => db.SaveChangesAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_request_status_outside_the_known_set_is_refused()
    {
        var tenantId = await SeedTenant();
        await using var db = AppContext(tenantId);
        db.GatewayRequests.Add(new GatewayRequestRecord
        {
            Id = Guid.NewGuid(), TenantId = tenantId, ClientId = Guid.NewGuid(), ActorType = "client", RequestId = "req_1",
            Action = "gateway.chat", Status = "maybe", CreatedAt = Hour,
        });

        var act = () => db.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }
}
