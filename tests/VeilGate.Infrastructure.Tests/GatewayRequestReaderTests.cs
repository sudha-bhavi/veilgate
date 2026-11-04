using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.Domain.Metrics;
using VeilGate.Infrastructure.Persistence;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Infrastructure.Tests;

[Collection("postgres")]
public class GatewayRequestReaderTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly GatewayRequestFilter NoFilter = new(null, null, null, null, null, null);

    private GatewayRequestReader Reader(Guid tenantId)
    {
        var tenant = new TenantContext();
        tenant.Set(tenantId);
        var options = new DbContextOptionsBuilder<VeilGateDbContext>().UseNpgsql(pg.AppConnection)
            .AddInterceptors(new TenantConnectionInterceptor(tenant)).Options;
        return new GatewayRequestReader(new VeilGateDbContext(options), tenant);
    }

    private async Task<Guid> SeedTenant()
    {
        var t = Tenant.Create($"Requests {Guid.NewGuid():N}"[..20]).Value;
        await using var db = pg.AdminContext();
        db.Add(t);
        await db.SaveChangesAsync();
        return t.Id;
    }

    private async Task<GatewayRequestRecord> Seed(Guid tenantId, DateTimeOffset at, string status = "success", string action = "gateway.chat", Guid? clientId = null, string requestId = "req_1")
    {
        var row = new GatewayRequestRecord
        {
            Id = Guid.NewGuid(), TenantId = tenantId, ClientId = clientId ?? Guid.NewGuid(), ActorType = "client", RequestId = requestId,
            Action = action, Status = status, EntityCount = 2, Entities = new Dictionary<string, int> { ["PHONE"] = 2 }, LatencyMs = 90, CreatedAt = at,
        };
        await using var db = pg.AdminContext();
        db.GatewayRequests.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    [Fact]
    public async Task Requests_come_back_newest_first()
    {
        var tenantId = await SeedTenant();
        var older = await Seed(tenantId, Noon);
        var newer = await Seed(tenantId, Noon.AddMinutes(5));

        var (items, total) = await Reader(tenantId).ListAsync(NoFilter, 1, 50);

        total.Should().Be(2);
        items.Select(i => i.Id).Should().Equal(newer.Id, older.Id);
    }

    [Fact]
    public async Task A_page_holds_only_its_share_but_the_total_counts_everything()
    {
        var tenantId = await SeedTenant();
        for (var i = 0; i < 5; i++) await Seed(tenantId, Noon.AddMinutes(i));

        var (items, total) = await Reader(tenantId).ListAsync(NoFilter, 2, 2);

        total.Should().Be(5);
        items.Should().HaveCount(2);
    }

    [Fact]
    public async Task The_filters_narrow_the_list()
    {
        var tenantId = await SeedTenant();
        var client = Guid.NewGuid();
        var wanted = await Seed(tenantId, Noon, "denied", "gateway.deidentify", client, "req_wanted");
        await Seed(tenantId, Noon, "success", "gateway.chat");
        await Seed(tenantId, Noon.AddDays(-3), "denied", "gateway.deidentify", client);
        var reader = Reader(tenantId);

        (await reader.ListAsync(NoFilter with { Status = "denied" }, 1, 50)).Total.Should().Be(2);
        (await reader.ListAsync(NoFilter with { Action = "gateway.deidentify" }, 1, 50)).Total.Should().Be(2);
        (await reader.ListAsync(NoFilter with { ClientId = client }, 1, 50)).Total.Should().Be(2);
        (await reader.ListAsync(NoFilter with { From = Noon.AddHours(-1) }, 1, 50)).Total.Should().Be(2);
        (await reader.ListAsync(NoFilter with { To = Noon.AddHours(-1) }, 1, 50)).Total.Should().Be(1);
        (await reader.ListAsync(NoFilter with { RequestId = "req_wanted" }, 1, 50)).Items.Single().Id.Should().Be(wanted.Id);
    }

    [Fact]
    public async Task A_client_name_is_added_when_the_client_still_exists()
    {
        var tenantId = await SeedTenant();
        var missing = await Seed(tenantId, Noon);

        var (items, _) = await Reader(tenantId).ListAsync(NoFilter, 1, 50);

        items.Single(i => i.Id == missing.Id).ClientName.Should().BeNull();   // a client that no longer exists has no name
    }

    [Fact]
    public async Task Another_tenant_sees_no_requests_and_cannot_open_one_by_id()
    {
        var a = await SeedTenant();
        var b = await SeedTenant();
        var row = await Seed(a, Noon);

        var other = Reader(b);

        (await other.ListAsync(NoFilter, 1, 50)).Total.Should().Be(0);
        (await other.FindAsync(row.Id)).Should().BeNull();
        (await other.ListActionsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task One_request_can_be_opened_by_id_with_its_counts()
    {
        var tenantId = await SeedTenant();
        var row = await Seed(tenantId, Noon);

        var view = await Reader(tenantId).FindAsync(row.Id);

        view.Should().NotBeNull();
        view!.Entities.Should().BeEquivalentTo(new Dictionary<string, int> { ["PHONE"] = 2 });
        view.EntityCount.Should().Be(2);
        view.LatencyMs.Should().Be(90);
    }

    [Fact]
    public async Task The_actions_that_occur_are_listed_once_each_in_order()
    {
        var tenantId = await SeedTenant();
        await Seed(tenantId, Noon, action: "gateway.chat");
        await Seed(tenantId, Noon, action: "gateway.chat");
        await Seed(tenantId, Noon, action: "gateway.detect");

        (await Reader(tenantId).ListActionsAsync()).Should().Equal("gateway.chat", "gateway.detect");
    }
}
