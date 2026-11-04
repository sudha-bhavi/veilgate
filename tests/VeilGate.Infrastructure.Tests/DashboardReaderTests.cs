using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using VeilGate.Domain;
using VeilGate.Domain.Metrics;
using VeilGate.Infrastructure.Persistence;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Infrastructure.Tests;

[Collection("postgres")]
public class DashboardReaderTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Hour = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);

    private DashboardReader Reader(Guid tenantId)
    {
        var tenant = new TenantContext();
        tenant.Set(tenantId);
        var options = new DbContextOptionsBuilder<VeilGateDbContext>().UseNpgsql(pg.AppConnection)
            .AddInterceptors(new TenantConnectionInterceptor(tenant)).Options;
        return new DashboardReader(new VeilGateDbContext(options), tenant);
    }

    private async Task<Guid> SeedTenant()
    {
        var t = Tenant.Create($"Dashboard {Guid.NewGuid():N}"[..20]).Value;
        await using var db = pg.AdminContext();
        db.Add(t);
        await db.SaveChangesAsync();
        return t.Id;
    }

    private async Task Seed(Guid tenantId, DateTimeOffset hour, Guid clientId, int requests, int blocked = 0, long entities = 0,
        Dictionary<string, int>? byType = null, Dictionary<string, int>? latency = null)
    {
        await using var db = pg.AdminContext();
        db.MetricsHourly.Add(new MetricsHour
        {
            TenantId = tenantId, Hour = hour, ClientId = clientId, Requests = requests, Blocked = blocked, Entities = entities,
            EntitiesByType = byType ?? new Dictionary<string, int>(), LatencyBuckets = latency ?? new Dictionary<string, int>(),
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task The_totals_add_up_every_hour_and_client_in_the_span()
    {
        var tenantId = await SeedTenant();
        await Seed(tenantId, Hour, Guid.NewGuid(), 10, blocked: 2, entities: 20, byType: new() { ["PHONE"] = 20 }, latency: new() { ["100"] = 10 });
        await Seed(tenantId, Hour.AddHours(1), Guid.NewGuid(), 5, entities: 4, byType: new() { ["PHONE"] = 1, ["SSN"] = 3 }, latency: new() { ["100"] = 4, ["250"] = 1 });

        var totals = await Reader(tenantId).TotalsAsync(Hour, Hour.AddHours(2));

        (totals.Requests, totals.Blocked, totals.Entities).Should().Be((15, 2, 24));
        totals.EntitiesByType.Should().BeEquivalentTo(new Dictionary<string, int> { ["PHONE"] = 21, ["SSN"] = 3 });
        totals.LatencyBuckets.Should().BeEquivalentTo(new Dictionary<string, int> { ["100"] = 14, ["250"] = 1 });
    }

    [Fact]
    public async Task The_start_hour_is_included_and_the_end_hour_is_not()
    {
        var tenantId = await SeedTenant();
        await Seed(tenantId, Hour.AddHours(-1), Guid.NewGuid(), 1);
        await Seed(tenantId, Hour, Guid.NewGuid(), 10);
        await Seed(tenantId, Hour.AddHours(1), Guid.NewGuid(), 100);

        var totals = await Reader(tenantId).TotalsAsync(Hour, Hour.AddHours(1));

        totals.Requests.Should().Be(10);
    }

    [Fact]
    public async Task A_span_with_no_activity_gives_zeros_not_an_error()
    {
        var tenantId = await SeedTenant();

        var totals = await Reader(tenantId).TotalsAsync(Hour, Hour.AddHours(24));

        (totals.Requests, totals.Entities).Should().Be((0, 0));
        totals.LatencyBuckets.Should().BeEmpty();
    }

    [Fact]
    public async Task The_top_callers_are_ranked_by_requests_across_hours()
    {
        var tenantId = await SeedTenant();
        var busy = Guid.NewGuid();
        var quiet = Guid.NewGuid();
        await Seed(tenantId, Hour, busy, 5);
        await Seed(tenantId, Hour.AddHours(1), busy, 6);
        await Seed(tenantId, Hour, quiet, 7);

        var top = await Reader(tenantId).TopClientsAsync(Hour, Hour.AddHours(2), 5);

        top.Select(t => (t.ClientId, t.Requests)).Should().Equal((busy, 11L), (quiet, 7L));
        top.Should().OnlyContain(t => t.Name == null);   // neither exists as a client or a person
    }

    [Fact]
    public async Task The_top_list_is_cut_to_the_number_asked_for()
    {
        var tenantId = await SeedTenant();
        for (var i = 1; i <= 4; i++) await Seed(tenantId, Hour, Guid.NewGuid(), i);

        (await Reader(tenantId).TopClientsAsync(Hour, Hour.AddHours(1), 2)).Select(t => t.Requests).Should().Equal(4L, 3L);
    }

    [Fact]
    public async Task The_hourly_points_sum_all_clients_of_an_hour()
    {
        var tenantId = await SeedTenant();
        await Seed(tenantId, Hour, Guid.NewGuid(), 3, entities: 6, latency: new() { ["100"] = 3 });
        await Seed(tenantId, Hour, Guid.NewGuid(), 4, entities: 2, latency: new() { ["100"] = 2, ["250"] = 2 });
        await Seed(tenantId, Hour.AddHours(1), Guid.NewGuid(), 1);

        var points = await Reader(tenantId).HourlyAsync(Hour, Hour.AddHours(2));

        points.Should().HaveCount(2);
        points[0].Hour.Should().Be(Hour);
        (points[0].Requests, points[0].Entities).Should().Be((7, 8));
        points[0].LatencyBuckets.Should().BeEquivalentTo(new Dictionary<string, int> { ["100"] = 5, ["250"] = 2 });
    }

    [Fact]
    public async Task Another_tenant_sees_none_of_it()
    {
        var a = await SeedTenant();
        var b = await SeedTenant();
        await Seed(a, Hour, Guid.NewGuid(), 10);

        var other = Reader(b);

        (await other.TotalsAsync(Hour, Hour.AddHours(1))).Requests.Should().Be(0);
        (await other.TopClientsAsync(Hour, Hour.AddHours(1), 5)).Should().BeEmpty();
        (await other.HourlyAsync(Hour, Hour.AddHours(1))).Should().BeEmpty();
    }

    [Fact]
    public async Task With_no_evaluation_there_is_no_leak_rate()
    {
        var tenantId = await SeedTenant();

        (await Reader(tenantId).LatestLeakRateAsync()).Should().BeNull();
    }
}
